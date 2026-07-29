using System.Text.Json;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ReleasePackageValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ReleaseMarker Validate(string rootPath, string? expectedVersion = null, bool validateTargetSafety = false)
    {
        string root = NormalizeRoot(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Release root does not exist: " + root);
        }

        if (validateTargetSafety)
        {
            ValidateTargetPath(root);
        }

        string markerPath = Path.Combine(root, "autoplayer-release.json");
        if (!File.Exists(markerPath))
        {
            throw new InvalidDataException("Release root is missing autoplayer-release.json.");
        }

        ReleaseMarker marker = JsonSerializer.Deserialize<ReleaseMarker>(File.ReadAllText(markerPath), JsonOptions)
                               ?? throw new InvalidDataException("Release marker is empty.");
        if (!SemanticVersion.TryParse(marker.Version, out _))
        {
            throw new InvalidDataException("Release marker version is invalid.");
        }

        if (!Version.TryParse(marker.BepInExVersion, out Version? bepinexVersion)
            || bepinexVersion.Major != 5)
        {
            throw new InvalidDataException("Release marker must identify a BepInEx 5 runtime.");
        }

        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !VersionsEqual(marker.Version, expectedVersion))
        {
            throw new InvalidDataException($"Release marker version {marker.Version} does not match manifest {expectedVersion}.");
        }

        RequireDirectory(root, "manager");
        RequireDirectory(root, "updater");
        RequireDirectory(root, "payload");
        RequireExecutableOrDll(root, "manager", "Loopstructor.AutoPlayer.Manager");
        RequireExecutableOrDll(root, "updater", "Loopstructor.AutoPlayer.Updater");

        string bepinexPayload = string.IsNullOrWhiteSpace(marker.BepInExPayloadPath)
            ? Path.Combine("payload", "bepinex")
            : marker.BepInExPayloadPath;
        string pluginPayload = string.IsNullOrWhiteSpace(marker.PluginPayloadPath)
            ? Path.Combine("payload", "plugin")
            : marker.PluginPayloadPath;
        string bepinexRoot = RequireSafeRelativeDirectory(root, bepinexPayload, "BepInEx payload");
        string pluginRoot = RequireSafeRelativeDirectory(root, pluginPayload, "plugin payload");
        RequireFile(bepinexRoot, "winhttp.dll");
        RequireFile(bepinexRoot, "doorstop_config.ini");
        RequireFile(bepinexRoot, Path.Combine("BepInEx", "core", "BepInEx.dll"));
        RequireFile(bepinexRoot, Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll"));
        RequireFile(pluginRoot, "Loopstructor.AutoPlayer.Plugin.dll");
        RequireFile(pluginRoot, "Loopstructor.AutoPlayer.Core.dll");
        ValidateChecksums(root);
        return marker;
    }

    public static string NormalizeRoot(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void ValidateTargetPath(string root)
    {
        string pathRoot = Path.GetPathRoot(root) ?? string.Empty;
        if (string.Equals(root, pathRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A drive root cannot be an update target.");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, userProfile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, windows, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected update target is too broad.");
        }

        DirectoryInfo info = new(root);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Release roots implemented as reparse points are not updated in place.");
        }
    }

    private static void RequireDirectory(string root, string relative)
    {
        if (!Directory.Exists(Path.Combine(root, relative)))
        {
            throw new InvalidDataException("Release root is missing directory: " + relative);
        }
    }

    private static void RequireExecutableOrDll(string root, string directory, string stem)
    {
        if (!File.Exists(Path.Combine(root, directory, stem + ".exe"))
            && !File.Exists(Path.Combine(root, directory, stem + ".dll")))
        {
            throw new InvalidDataException($"Release root is missing {directory}/{stem}.exe or .dll.");
        }
    }

    private static string RequireSafeRelativeDirectory(string root, string relative, string label)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(label + " path must be relative.");
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
        {
            throw new InvalidDataException(label + " directory is missing or outside the release root.");
        }

        return full;
    }

    private static void RequireFile(string root, string relative)
    {
        if (!File.Exists(Path.Combine(root, relative)))
        {
            throw new InvalidDataException("Release payload is missing required file: " + relative.Replace('\\', '/'));
        }
    }

    private static void ValidateChecksums(string root)
    {
        string checksumPath = Path.Combine(root, "checksums.sha256");
        if (!File.Exists(checksumPath) || new FileInfo(checksumPath).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Release root is missing a reasonably sized checksums.sha256 file.");
        }

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (string rawLine in File.ReadLines(checksumPath))
        {
            string line = rawLine.TrimEnd();
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
            {
                throw new InvalidDataException("checksums.sha256 contains an invalid line.");
            }

            string hash = line[..64];
            string relative = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (hash.Any(character => !Uri.IsHexDigit(character))
                || Path.IsPathRooted(relative)
                || string.IsNullOrWhiteSpace(relative))
            {
                throw new InvalidDataException("checksums.sha256 contains an unsafe entry.");
            }

            string fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)
                || !expected.TryAdd(Path.GetRelativePath(root, fullPath), hash.ToLowerInvariant()))
            {
                throw new InvalidDataException("checksums.sha256 references a missing, duplicate, or outside file: " + relative);
            }
        }

        foreach ((string relative, string expectedHash) in expected)
        {
            string fullPath = Path.Combine(root, relative);
            using FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 sha256 = SHA256.Create();
            string actual = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Release checksum mismatch: " + relative.Replace('\\', '/'));
            }
        }

        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (string.Equals(relative, "checksums.sha256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "autoplayer-update.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!expected.ContainsKey(relative))
            {
                throw new InvalidDataException("Release contains an unlisted file: " + relative.Replace('\\', '/'));
            }
        }
    }

    private static bool VersionsEqual(string left, string right)
    {
        return SemanticVersion.TryParse(left, out SemanticVersion? leftVersion)
               && SemanticVersion.TryParse(right, out SemanticVersion? rightVersion)
               && leftVersion!.CompareTo(rightVersion) == 0;
    }
}
