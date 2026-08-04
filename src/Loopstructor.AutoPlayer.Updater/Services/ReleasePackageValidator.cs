using System.Text.Json;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ReleasePackageValidator
{
    internal const string RequiredManagerEntryPoint = "Loopstructor.AutoPlayer.Manager.exe";
    internal const string RequiredUpdaterEntryPoint = "manager/Loopstructor.AutoPlayer.Updater.exe";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ReleaseMarker Validate(string rootPath, string? expectedVersion = null, bool validateTargetSafety = false)
    {
        string root = NormalizeRoot(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("发布目录不存在：" + root);
        }

        if (validateTargetSafety)
        {
            ValidateTargetPath(root);
        }

        ReleaseMarker marker = ReadMarker(root);
        if (!SemanticVersion.TryParse(marker.Version, out _))
        {
            throw new InvalidDataException("发布标记中的版本无效。");
        }

        if (!Version.TryParse(marker.BepInExVersion, out Version? bepinexVersion)
            || bepinexVersion.Major != 5)
        {
            throw new InvalidDataException("发布标记必须指定 BepInEx 5 运行时。");
        }

        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !VersionsEqual(marker.Version, expectedVersion))
        {
            throw new InvalidDataException($"发布标记版本 {marker.Version} 与清单版本 {expectedVersion} 不一致。");
        }

        RequireDirectory(root, "manager");
        RequireDirectory(root, "payload");
        if (Directory.Exists(Path.Combine(root, "updater")))
        {
            throw new InvalidDataException("发布目录不能包含已停用的 updater 兼容目录。");
        }
        RequireExecutableOrDll(root, "manager", "Loopstructor.AutoPlayer.Manager");
        ResolveRequiredEntryPoint(
            root,
            marker.ManagerPath,
            RequiredManagerEntryPoint,
            "Manager 入口");
        ResolveRequiredEntryPoint(
            root,
            marker.UpdaterPath,
            RequiredUpdaterEntryPoint,
            "Updater 入口");

        string bepinexPayload = string.IsNullOrWhiteSpace(marker.BepInExPayloadPath)
            ? Path.Combine("payload", "bepinex")
            : marker.BepInExPayloadPath;
        string pluginPayload = string.IsNullOrWhiteSpace(marker.PluginPayloadPath)
            ? Path.Combine("payload", "plugin")
            : marker.PluginPayloadPath;
        string bepinexRoot = RequireSafeRelativeDirectory(root, bepinexPayload, "BepInEx 载荷");
        string pluginRoot = RequireSafeRelativeDirectory(root, pluginPayload, "插件载荷");
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

    internal static ReleaseMarker ReadMarker(string rootPath)
    {
        string root = NormalizeRoot(rootPath);
        string markerPath = Path.Combine(root, "autoplayer-release.json");
        if (!File.Exists(markerPath))
        {
            throw new InvalidDataException("发布目录缺少 autoplayer-release.json。");
        }

        return JsonSerializer.Deserialize<ReleaseMarker>(File.ReadAllText(markerPath), JsonOptions)
               ?? throw new InvalidDataException("发布标记为空。");
    }

    internal static string ResolveRequiredEntryPoint(
        string rootPath,
        string configuredPath,
        string requiredPath,
        string label)
    {
        string root = NormalizeRoot(rootPath);
        if (!string.Equals(configuredPath, requiredPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(label + "必须是 " + requiredPath + "。");
        }

        return RequireSafeRelativeFile(root, requiredPath, label);
    }

    private static void ValidateTargetPath(string root)
    {
        string pathRoot = Path.GetPathRoot(root) ?? string.Empty;
        if (string.Equals(root, pathRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("磁盘根目录不能作为更新目标。");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, userProfile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, windows, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("选择的更新目标范围过大。");
        }

        DirectoryInfo info = new(root);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("不支持就地更新作为重解析点的发布目录。");
        }
    }

    private static void RequireDirectory(string root, string relative)
    {
        if (!Directory.Exists(Path.Combine(root, relative)))
        {
            throw new InvalidDataException("发布目录缺少子目录：" + relative);
        }
    }

    private static void RequireExecutableOrDll(string root, string directory, string stem)
    {
        if (!File.Exists(Path.Combine(root, directory, stem + ".exe"))
            && !File.Exists(Path.Combine(root, directory, stem + ".dll")))
        {
            throw new InvalidDataException($"发布目录缺少 {directory}/{stem}.exe 或 .dll。");
        }
    }

    private static string RequireSafeRelativeDirectory(string root, string relative, string label)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(label + "路径必须是相对路径。");
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
        {
            throw new InvalidDataException(label + "目录不存在或超出发布目录。");
        }

        return full;
    }

    private static string RequireSafeRelativeFile(string root, string relative, string label)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(label + "路径必须是相对路径。");
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new InvalidDataException(label + "不存在或超出发布目录。");
        }

        return full;
    }

    private static void RequireFile(string root, string relative)
    {
        if (!File.Exists(Path.Combine(root, relative)))
        {
            throw new InvalidDataException("发布载荷缺少必要文件：" + relative.Replace('\\', '/'));
        }
    }

    private static void ValidateChecksums(string root)
    {
        string checksumPath = Path.Combine(root, "checksums.sha256");
        if (!File.Exists(checksumPath) || new FileInfo(checksumPath).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("发布目录缺少大小合理的 checksums.sha256 文件。");
        }

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (string rawLine in File.ReadLines(checksumPath))
        {
            string line = rawLine.TrimEnd();
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
            {
                throw new InvalidDataException("checksums.sha256 包含无效行。");
            }

            string hash = line[..64];
            string relative = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (hash.Any(character => !Uri.IsHexDigit(character))
                || Path.IsPathRooted(relative)
                || string.IsNullOrWhiteSpace(relative))
            {
                throw new InvalidDataException("checksums.sha256 包含不安全的条目。");
            }

            string fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)
                || !expected.TryAdd(Path.GetRelativePath(root, fullPath), hash.ToLowerInvariant()))
            {
                throw new InvalidDataException("checksums.sha256 引用了缺失、重复或超出发布目录的文件：" + relative);
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
                throw new InvalidDataException("发布文件校验和不匹配：" + relative.Replace('\\', '/'));
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
                throw new InvalidDataException("发布目录包含未列入校验清单的文件：" + relative.Replace('\\', '/'));
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
