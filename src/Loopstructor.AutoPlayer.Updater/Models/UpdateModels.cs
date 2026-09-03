using System.Text.Json.Serialization;

namespace Loopstructor.AutoPlayer.Updater.Models;

public enum UpdateCommand
{
    Check,
    Apply,
    Cleanup
}

public sealed class UpdateCommandOptions
{
    public UpdateCommand Command { get; private set; }
    public bool JsonOutput { get; private set; }
    public bool JsonStream { get; private set; }
    public bool RestartManager { get; private set; }
    public bool StagedRun { get; private set; }
    public string CurrentVersion { get; private set; } = "0.0.0";
    public string TargetRoot { get; private set; } = string.Empty;
    public string ConfigPath { get; private set; } = string.Empty;
    public int WaitTimeoutSeconds { get; private set; } = 600;
    public IList<int> WaitProcessIds { get; } = new List<int>();

    public static UpdateCommandOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("缺少更新器命令，应使用 check、apply 或 cleanup。");
        }

        UpdateCommandOptions result = new()
        {
            Command = args[0].ToLowerInvariant() switch
            {
                "check" => UpdateCommand.Check,
                "apply" => UpdateCommand.Apply,
                "cleanup" => UpdateCommand.Cleanup,
                _ => throw new ArgumentException("未知的更新器命令：" + args[0])
            }
        };

        for (int index = 1; index < args.Count; index++)
        {
            string current = args[index];
            switch (current.ToLowerInvariant())
            {
                case "--json":
                    result.JsonOutput = true;
                    break;
                case "--json-stream":
                    result.JsonOutput = true;
                    result.JsonStream = true;
                    break;
                case "--restart-manager":
                    result.RestartManager = true;
                    break;
                case "--staged-run":
                    result.StagedRun = true;
                    break;
                case "--current-version":
                    result.CurrentVersion = NextValue(args, ref index, current);
                    break;
                case "--target":
                    result.TargetRoot = Path.GetFullPath(NextValue(args, ref index, current));
                    break;
                case "--config":
                    result.ConfigPath = Path.GetFullPath(NextValue(args, ref index, current));
                    break;
                case "--wait-pid":
                    string processValue = NextValue(args, ref index, current);
                    if (!int.TryParse(processValue, out int processId) || processId <= 0)
                    {
                        throw new ArgumentException("进程 ID 无效：" + processValue);
                    }

                    if (processId != Environment.ProcessId && !result.WaitProcessIds.Contains(processId))
                    {
                        result.WaitProcessIds.Add(processId);
                    }

                    break;
                case "--wait-timeout-seconds":
                    string timeoutValue = NextValue(args, ref index, current);
                    if (!int.TryParse(timeoutValue, out int timeoutSeconds) || timeoutSeconds is < 10 or > 3600)
                    {
                        throw new ArgumentException("等待超时必须介于 10 到 3600 秒之间。");
                    }

                    result.WaitTimeoutSeconds = timeoutSeconds;
                    break;
                default:
                    throw new ArgumentException("未知的更新器选项：" + current);
            }
        }

        if (!SemanticVersion.TryParse(result.CurrentVersion, out _))
        {
            throw new ArgumentException("当前版本不是有效的 SemVer：" + result.CurrentVersion);
        }

        if (result.Command is UpdateCommand.Apply or UpdateCommand.Cleanup
            && string.IsNullOrWhiteSpace(result.TargetRoot))
        {
            throw new ArgumentException(
                result.Command == UpdateCommand.Apply
                    ? "apply 命令必须提供 --target <release-root>。"
                    : "cleanup 命令必须提供 --target <release-root>。");
        }

        return result;
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException(option + " 需要提供参数值。");
        }

        return args[++index];
    }
}

public sealed class UpdateSourceSettings
{
    public const string DefaultGitHubOwner = "yingyu4451";
    public const string DefaultGitHubRepository = "Loopstructor-2-QA-Tool";
    public const string LegacyGitHubOwner = "yingyu4451";
    public const string LegacyGitHubRepository = "gui2";
    public const string PreviousGitHubRepository = "Loopstructor-2-AutoPlayer";

    public string GitHubOwner { get; set; } = DefaultGitHubOwner;
    public string GitHubRepository { get; set; } = DefaultGitHubRepository;
    public string RuntimeIdentifier { get; set; } = "win-x64";
    public string ManifestAssetName { get; set; } = "autoplayer-update-manifest.json";

    public void NormalizeBuiltInRepositoryRename()
    {
        GitHubOwner = string.IsNullOrWhiteSpace(GitHubOwner) ? DefaultGitHubOwner : GitHubOwner.Trim();
        GitHubRepository = string.IsNullOrWhiteSpace(GitHubRepository)
            ? DefaultGitHubRepository
            : GitHubRepository.Trim();

        if (string.Equals(GitHubOwner, LegacyGitHubOwner, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(GitHubRepository, LegacyGitHubRepository, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GitHubRepository, PreviousGitHubRepository, StringComparison.OrdinalIgnoreCase)))
        {
            GitHubOwner = DefaultGitHubOwner;
            GitHubRepository = DefaultGitHubRepository;
        }
    }
}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ReleaseNotesUrl { get; set; } = string.Empty;
    public List<UpdateDeltaAsset> DeltaAssets { get; set; } = new();
}

public sealed class UpdateDeltaAsset
{
    public string FromVersion { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class GitHubReleaseAsset
{
    public string Name { get; init; } = string.Empty;
    public Uri DownloadUri { get; init; } = null!;
    public long Size { get; init; }
}

public sealed class ResolvedUpdate
{
    public required UpdateManifest Manifest { get; init; }
    public required GitHubReleaseAsset PackageAsset { get; init; }
    public IReadOnlyList<ResolvedDeltaPackage> DeltaPackages { get; init; } = Array.Empty<ResolvedDeltaPackage>();
    public string ReleaseTag { get; init; } = string.Empty;
    public string ReleasePageUrl { get; init; } = string.Empty;
}

public sealed class ResolvedDeltaPackage
{
    public required UpdateDeltaAsset Manifest { get; init; }
    public required GitHubReleaseAsset PackageAsset { get; init; }
}

public sealed class UpdaterResult
{
    public bool Success { get; set; }
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
    public bool ManagerRestartFailed { get; set; }
    public bool UsedIncrementalUpdate { get; set; }
}

public sealed class ReleaseMarker
{
    public string Version { get; set; } = string.Empty;
    public string BepInExVersion { get; set; } = string.Empty;
    public string ManagerPath { get; set; } = string.Empty;
    public string UpdaterPath { get; set; } = string.Empty;
    public string BepInExPayloadPath { get; set; } = string.Empty;
    public string PluginPayloadPath { get; set; } = string.Empty;
}

public sealed class UpdateTransactionJournal
{
    public string TransactionId { get; set; } = string.Empty;
    public string TargetRoot { get; set; } = string.Empty;
    public string StagingRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<GitHubAssetResponse> Assets { get; set; } = new();
}

internal sealed class GitHubAssetResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
