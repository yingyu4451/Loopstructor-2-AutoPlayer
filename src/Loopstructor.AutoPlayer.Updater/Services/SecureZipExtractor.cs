using System.IO.Compression;
using System.Diagnostics;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class SecureZipExtractor
{
    public const string ReleaseArchiveRootDirectory = "Loopstructor-2-QA-Tool";
    public const string DeltaArchiveRootDirectory = "Loopstructor-2-QA-Tool.delta";
    public const int MaximumEntryCount = 10_000;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumSingleFileBytes = 512L * 1024 * 1024;
    public const int MaximumCompressionRatio = 500;

    public void Extract(string archivePath, string destinationRoot)
    {
        ExtractCore(archivePath, destinationRoot, requiredRootDirectory: null, progress: null, default);
    }

    public void ExtractReleasePackage(string archivePath, string destinationRoot)
    {
        ExtractCore(archivePath, destinationRoot, ReleaseArchiveRootDirectory, progress: null, default);
    }

    public void ExtractReleasePackage(
        string archivePath,
        string destinationRoot,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken cancellationToken = default) =>
        ExtractCore(archivePath, destinationRoot, ReleaseArchiveRootDirectory, progress, cancellationToken);

    public void ExtractDeltaPackage(
        string archivePath,
        string destinationRoot,
        IProgress<ArchiveExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExtractCore(archivePath, destinationRoot, DeltaArchiveRootDirectory, progress, cancellationToken);

    private static void ExtractCore(
        string archivePath,
        string destinationRoot,
        string? requiredRootDirectory,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string archive = Path.GetFullPath(archivePath);
        string destination = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException("找不到更新安装包。", archive);
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("ZIP 解压目标目录必须为空：" + destination);
        }

        Directory.CreateDirectory(destination);
        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("ZIP 条目数量超出允许范围。");
        }

        string destinationPrefix = destination + Path.DirectorySeparatorChar;
        Dictionary<string, ValidatedEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        long expandedTotal = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                throw new InvalidDataException("ZIP 包含重复路径：" + validated.RelativePath);
            }

            if (!validated.IsDirectory)
            {
                if (entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("ZIP 条目过大：" + entry.FullName);
                }

                expandedTotal = checked(expandedTotal + entry.Length);
                if (expandedTotal > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("ZIP 解压后的总大小超过安全限制。");
                }

                if (entry.Length > 1024 * 1024
                    && entry.CompressedLength > 0
                    && entry.Length / entry.CompressedLength > MaximumCompressionRatio)
                {
                    throw new InvalidDataException("ZIP 条目的压缩比不安全：" + entry.FullName);
                }
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("ZIP 中没有任何安装包文件。");
        }

        foreach (ValidatedEntry entry in entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? parent = Path.GetDirectoryName(entry.RelativePath);
            while (!string.IsNullOrWhiteSpace(parent))
            {
                if (entries.TryGetValue(parent, out ValidatedEntry? ancestor) && !ancestor.IsDirectory)
                {
                    throw new InvalidDataException("ZIP 文件与其子路径冲突：" + ancestor.RelativePath);
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        int totalFiles = entries.Values.Count(item => !item.IsDirectory);
        int extractedFiles = 0;
        long extractedBytes = 0;
        TimeSpan lastReportedAt = TimeSpan.Zero;
        Stopwatch extractionClock = Stopwatch.StartNew();
        ReportProgressSafely(progress, new ArchiveExtractionProgress(0, expandedTotal, 0, totalFiles));
        foreach (ValidatedEntry validated in entries.Values.OrderBy(item => item.IsDirectory ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                written += read;
                extractedBytes += read;
                if (written > validated.Entry.Length || written > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("ZIP 条目的解压大小超过声明长度：" + validated.Entry.FullName);
                }

                output.Write(buffer, 0, read);
                TimeSpan elapsed = extractionClock.Elapsed;
                if (elapsed - lastReportedAt >= TimeSpan.FromMilliseconds(100))
                {
                    ReportProgressSafely(
                        progress,
                        new ArchiveExtractionProgress(extractedBytes, expandedTotal, extractedFiles, totalFiles));
                    lastReportedAt = elapsed;
                }
            }

            if (written != validated.Entry.Length)
            {
                throw new InvalidDataException("ZIP 条目在解压时长度发生变化：" + validated.Entry.FullName);
            }

            try
            {
                File.SetLastWriteTimeUtc(validated.FullPath, validated.Entry.LastWriteTime.UtcDateTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid archive timestamps are non-authoritative metadata.
            }

            extractedFiles++;
            ReportProgressSafely(
                progress,
                new ArchiveExtractionProgress(extractedBytes, expandedTotal, extractedFiles, totalFiles));
        }
    }

    private static void ReportProgressSafely(
        IProgress<ArchiveExtractionProgress>? progress,
        ArchiveExtractionProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch
        {
            // Progress display is non-authoritative and must never weaken archive validation.
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
            throw new InvalidDataException("ZIP 包含无效路径：" + entry.FullName);
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("ZIP 包含不安全的路径段：" + entry.FullName);
            }
        }

        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        FileAttributes windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("ZIP 不允许包含符号链接或重解析点：" + entry.FullName);
        }

        if (requiredRootDirectory is not null)
        {
            if (!string.Equals(segments[0], requiredRootDirectory, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"ZIP 条目必须位于名称完全匹配的“{requiredRootDirectory}”目录内：{entry.FullName}");
            }

            if (segments.Length == 1)
            {
                if (!isDirectory)
                {
                    throw new InvalidDataException("发布压缩包的根条目必须是目录：" + entry.FullName);
                }

                return null;
            }

            segments = segments[1..];
        }

        string relative = Path.Combine(segments);
        string fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ZIP 路径超出暂存目录：" + entry.FullName);
        }

        return new ValidatedEntry(entry, relative, fullPath, isDirectory);
    }

    private sealed record ValidatedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        string FullPath,
        bool IsDirectory);
}
