using System.Text.Json;
using System.Text.RegularExpressions;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class LoadedUpdateConfiguration
{
    public required UpdateSourceSettings Source { get; init; }
    public string GitHubToken { get; init; } = string.Empty;
    public string ConfigurationPath { get; init; } = string.Empty;
}

public sealed class UpdateConfigurationLoader
{
    public const string GitHubOwnerEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_GITHUB_OWNER";
    public const string GitHubRepositoryEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_GITHUB_REPOSITORY";
    public const string GitHubTokenEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_GITHUB_TOKEN";

    private static readonly Regex CoordinatePattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LoadedUpdateConfiguration Load(UpdateCommandOptions options, string? applicationDirectory = null)
    {
        string configPath = ResolveConfigPath(options, applicationDirectory ?? AppContext.BaseDirectory);
        UpdateSourceSettings source = new();
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            source = JsonSerializer.Deserialize<UpdateSourceSettings>(File.ReadAllText(configPath), JsonOptions)
                     ?? new UpdateSourceSettings();
        }

        string? ownerOverride = Environment.GetEnvironmentVariable(GitHubOwnerEnvironmentVariable);
        string? repositoryOverride = Environment.GetEnvironmentVariable(GitHubRepositoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(ownerOverride)) source.GitHubOwner = ownerOverride.Trim();
        if (!string.IsNullOrWhiteSpace(repositoryOverride)) source.GitHubRepository = repositoryOverride.Trim();

        if (!CoordinatePattern.IsMatch(source.GitHubOwner))
        {
            throw new InvalidOperationException(
                $"GitHub 仓库所有者配置无效。请设置 {GitHubOwnerEnvironmentVariable} 或检查 autoplayer-update.json。");
        }

        if (!CoordinatePattern.IsMatch(source.GitHubRepository))
        {
            throw new InvalidOperationException(
                $"GitHub 仓库名称配置无效。请设置 {GitHubRepositoryEnvironmentVariable} 或检查 autoplayer-update.json。");
        }

        if (!string.Equals(source.RuntimeIdentifier, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("此更新器仅支持 win-x64 发布通道。");
        }

        if (string.IsNullOrWhiteSpace(source.ManifestAssetName)
            || !string.Equals(Path.GetFileName(source.ManifestAssetName), source.ManifestAssetName, StringComparison.Ordinal)
            || !source.ManifestAssetName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ManifestAssetName 必须是不含目录的 JSON 文件名。");
        }

        return new LoadedUpdateConfiguration
        {
            Source = source,
            GitHubToken = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable)
                          ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                          ?? string.Empty,
            ConfigurationPath = configPath
        };
    }

    private static string ResolveConfigPath(UpdateCommandOptions options, string applicationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            if (!File.Exists(options.ConfigPath))
            {
                throw new FileNotFoundException("找不到更新器配置文件。", options.ConfigPath);
            }

            return options.ConfigPath;
        }

        List<string> candidates = new();
        if (!string.IsNullOrWhiteSpace(options.TargetRoot))
        {
            candidates.Add(Path.Combine(options.TargetRoot, "autoplayer-update.json"));
        }

        string baseDirectory = Path.GetFullPath(applicationDirectory);
        candidates.Add(Path.Combine(baseDirectory, "autoplayer-update.json"));

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }
}
