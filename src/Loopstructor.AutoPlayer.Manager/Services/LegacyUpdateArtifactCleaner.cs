using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

internal static class LegacyUpdateArtifactCleaner
{
    private const string BackupPrefix = ".LoopstructorAutoPlayer-backup-";
    private const string RollbackPrefix = ".LoopstructorAutoPlayer-rollback-";
    private const int MaximumEntryCount = 20000;

    internal static IReadOnlyList<string> CleanupAfterUpdate(string releaseRoot)
    {
        string root = NormalizeDirectory(releaseRoot);
        string parent = Directory.GetParent(root)?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parent)) return Array.Empty<string>();

        List<string> removed = new();
        foreach (string candidate in Directory.EnumerateDirectories(parent, ".LoopstructorAutoPlayer-*"))
        {
            try
            {
                string full = NormalizeDirectory(candidate);
                string name = Path.GetFileName(full);
                if (!string.Equals(Directory.GetParent(full)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
                    || (!IsGeneratedName(name, BackupPrefix) && !IsGeneratedName(name, RollbackPrefix))
                    || !LooksLikeReleaseRoot(full)
                    || !HasValidChecksums(full))
                {
                    continue;
                }

                if (TryDeleteDirectory(full)) removed.Add(full);
            }
            catch
            {
                // Cleanup must never prevent the newly installed Manager from starting.
            }
        }

        return removed;
    }

    internal static bool IsGeneratedName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string suffix = name[prefix.Length..];
        if (suffix.Length != 24 || suffix[8] != '-' || suffix[15] != '-') return false;
        return DateTime.TryParseExact(
                   suffix[..15],
                   "yyyyMMdd-HHmmss",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _) &&
               suffix[16..].All(Uri.IsHexDigit);
    }

    private static bool LooksLikeReleaseRoot(string root)
    {
        string markerPath = Path.Combine(root, "autoplayer-release.json");
        if (!File.Exists(markerPath)
            || !File.Exists(Path.Combine(root, "checksums.sha256"))
            || !File.Exists(Path.Combine(root, "Loopstructor.AutoPlayer.Manager.exe"))
            || !Directory.Exists(Path.Combine(root, "manager"))
            || !Directory.Exists(Path.Combine(root, "payload")))
        {
            return false;
        }

        using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        JsonElement version = marker.RootElement.EnumerateObject()
            .FirstOrDefault(property => string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase))
            .Value;
        return version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString());
    }

    private static bool HasValidChecksums(string root)
    {
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return false;
        string checksumPath = Path.Combine(root, "checksums.sha256");
        FileInfo checksumFile = new(checksumPath);
        if (!checksumFile.Exists || checksumFile.Length > 2 * 1024 * 1024) return false;

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (string rawLine in File.ReadLines(checksumPath))
        {
            string line = rawLine.TrimEnd();
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') return false;
            string hash = line[..64].ToLowerInvariant();
            string portableRelative = line[66..];
            if (hash.Any(character => !Uri.IsHexDigit(character))
                || portableRelative.Contains('\\')
                || Path.IsPathRooted(portableRelative)
                || string.IsNullOrWhiteSpace(portableRelative))
            {
                return false;
            }

            string[] segments = portableRelative.Split('/');
            if (segments.Any(segment => segment.Length == 0
                                        || segment is "." or ".."
                                        || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return false;
            }

            string relative = Path.Combine(segments);
            string fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "checksums.sha256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "autoplayer-update.json", StringComparison.OrdinalIgnoreCase)
                || !expected.TryAdd(relative, hash))
            {
                return false;
            }
        }

        if (expected.Count == 0) return false;
        int entryCount = 0;
        int verifiedFileCount = 0;
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++entryCount > MaximumEntryCount) return false;
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }

                string relative = Path.GetRelativePath(root, entry);
                if (string.Equals(relative, "checksums.sha256", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relative, "autoplayer-update.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!expected.TryGetValue(relative, out string? expectedHash)) return false;
                using FileStream stream = File.Open(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal)) return false;
                verifiedFileCount++;
            }
        }

        return verifiedFileCount == expected.Count;
    }

    private static bool TryDeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        return !Directory.Exists(path);
    }

    private static string NormalizeDirectory(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
