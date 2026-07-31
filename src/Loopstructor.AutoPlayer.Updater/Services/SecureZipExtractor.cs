using System.IO.Compression;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class SecureZipExtractor
{
    public const string ReleaseArchiveRootDirectory = "Loopstructor 2.AutoPlayer";
    public const int MaximumEntryCount = 10_000;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumSingleFileBytes = 512L * 1024 * 1024;
    public const int MaximumCompressionRatio = 500;

    public void Extract(string archivePath, string destinationRoot)
    {
        ExtractCore(archivePath, destinationRoot, requiredRootDirectory: null);
    }

    public void ExtractReleasePackage(string archivePath, string destinationRoot)
    {
        ExtractCore(archivePath, destinationRoot, ReleaseArchiveRootDirectory);
    }

    private static void ExtractCore(string archivePath, string destinationRoot, string? requiredRootDirectory)
    {
        string archive = Path.GetFullPath(archivePath);
        string destination = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException("Update package archive was not found.", archive);
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("ZIP destination must be empty: " + destination);
        }

        Directory.CreateDirectory(destination);
        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("ZIP entry count is outside the accepted range.");
        }

        string destinationPrefix = destination + Path.DirectorySeparatorChar;
        Dictionary<string, ValidatedEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        long expandedTotal = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ValidatedEntry? validated = ValidateEntry(
                entry,
                destination,
                destinationPrefix,
                requiredRootDirectory);
            if (validated is null)
            {
                continue;
            }

            if (!entries.TryAdd(validated.RelativePath, validated))
            {
                throw new InvalidDataException("ZIP contains duplicate path: " + validated.RelativePath);
            }

            if (!validated.IsDirectory)
            {
                if (entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("ZIP entry is too large: " + entry.FullName);
                }

                expandedTotal = checked(expandedTotal + entry.Length);
                if (expandedTotal > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("ZIP expanded size exceeds the safety limit.");
                }

                if (entry.Length > 1024 * 1024
                    && entry.CompressedLength > 0
                    && entry.Length / entry.CompressedLength > MaximumCompressionRatio)
                {
                    throw new InvalidDataException("ZIP entry has an unsafe compression ratio: " + entry.FullName);
                }
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("ZIP does not contain any package files.");
        }

        foreach (ValidatedEntry entry in entries.Values)
        {
            string? parent = Path.GetDirectoryName(entry.RelativePath);
            while (!string.IsNullOrWhiteSpace(parent))
            {
                if (entries.TryGetValue(parent, out ValidatedEntry? ancestor) && !ancestor.IsDirectory)
                {
                    throw new InvalidDataException("ZIP file conflicts with a child path: " + ancestor.RelativePath);
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        foreach (ValidatedEntry validated in entries.Values.OrderBy(item => item.IsDirectory ? 0 : 1))
        {
            if (validated.IsDirectory)
            {
                Directory.CreateDirectory(validated.FullPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(validated.FullPath)!);
            using Stream input = validated.Entry.Open();
            using FileStream output = new(
                validated.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                written += read;
                if (written > validated.Entry.Length || written > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("ZIP entry expanded beyond its declared length: " + validated.Entry.FullName);
                }

                output.Write(buffer, 0, read);
            }

            if (written != validated.Entry.Length)
            {
                throw new InvalidDataException("ZIP entry length changed while extracting: " + validated.Entry.FullName);
            }

            try
            {
                File.SetLastWriteTimeUtc(validated.FullPath, validated.Entry.LastWriteTime.UtcDateTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid archive timestamps are non-authoritative metadata.
            }
        }
    }

    private static ValidatedEntry? ValidateEntry(
        ZipArchiveEntry entry,
        string root,
        string rootPrefix,
        string? requiredRootDirectory)
    {
        string normalized = entry.FullName.Replace('\\', '/');
        bool isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
        normalized = normalized.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains('\0'))
        {
            throw new InvalidDataException("ZIP contains an invalid path: " + entry.FullName);
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("ZIP contains an unsafe path segment: " + entry.FullName);
            }
        }

        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        FileAttributes windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("ZIP symbolic links and reparse points are not accepted: " + entry.FullName);
        }

        if (requiredRootDirectory is not null)
        {
            if (!string.Equals(segments[0], requiredRootDirectory, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"ZIP entries must be inside the exact '{requiredRootDirectory}' directory: {entry.FullName}");
            }

            if (segments.Length == 1)
            {
                if (!isDirectory)
                {
                    throw new InvalidDataException("The release archive root must be a directory: " + entry.FullName);
                }

                return null;
            }

            segments = segments[1..];
        }

        string relative = Path.Combine(segments);
        string fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ZIP path escapes the staging directory: " + entry.FullName);
        }

        return new ValidatedEntry(entry, relative, fullPath, isDirectory);
    }

    private sealed record ValidatedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        string FullPath,
        bool IsDirectory);
}
