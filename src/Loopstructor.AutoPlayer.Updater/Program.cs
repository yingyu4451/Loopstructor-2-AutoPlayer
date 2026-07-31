using System.Net;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Updater;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        bool wantsJson = args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        UpdateCommandOptions? options = null;
        try
        {
            options = UpdateCommandOptions.Parse(args);
            if (options.Command == UpdateCommand.Apply && !options.StagedRun)
            {
                SelfRelocator relocator = new();
                relocator.RelaunchFromTemporaryCopy(args);
                return 0;
            }

            UpdateConfigurationLoader configurationLoader = new();
            LoadedUpdateConfiguration configuration = configurationLoader.Load(options);
            using SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20)
            };
            using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromMinutes(10) };
            GitHubReleaseClient releaseClient = new(httpClient, configuration.Source, configuration.GitHubToken);
            ResolvedUpdate update = await releaseClient.ResolveLatestAsync();
            if (!SemanticVersion.TryParse(options.CurrentVersion, out SemanticVersion? current)
                || !SemanticVersion.TryParse(update.Manifest.Version, out SemanticVersion? latest))
            {
                throw new InvalidDataException("当前版本或最新发布版本无效。");
            }

            bool updateAvailable = latest!.CompareTo(current) > 0;
            if (options.Command == UpdateCommand.Check)
            {
                UpdaterResult checkResult = new()
                {
                    Success = true,
                    UpdateAvailable = updateAvailable,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = updateAvailable
                        ? $"发现 AutoPlayer {update.Manifest.Version} 新版本。"
                        : "AutoPlayer 已是最新版本。"
                };
                WriteResult(checkResult, options.JsonOutput);
                return 0;
            }

            if (!updateAvailable)
            {
                WriteResult(new UpdaterResult
                {
                    Success = true,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = "无需更新。"
                }, options.JsonOutput);
                return 0;
            }

            return await ApplyAsync(options, update, releaseClient);
        }
        catch (Exception exception)
        {
            UpdaterResult failure = new()
            {
                CurrentVersion = options?.CurrentVersion ?? string.Empty,
                Message = GetUserFacingFailureMessage(exception)
            };
            WriteResult(failure, options?.JsonOutput ?? wantsJson);
            return 1;
        }
    }

    private static async Task<int> ApplyAsync(
        UpdateCommandOptions options,
        ResolvedUpdate update,
        GitHubReleaseClient releaseClient)
    {
        ReleasePackageValidator packageValidator = new();
        packageValidator.Validate(options.TargetRoot, validateTargetSafety: true);
        TransactionalInstaller installer = new(
            packageValidator,
            TransactionalInstaller.GetDefaultJournalPath(options.TargetRoot));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerUpdater",
            "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string packagePath = Path.Combine(temporaryRoot, update.Manifest.AssetName);
        string stagingRoot = installer.CreateStagingRoot(options.TargetRoot);
        bool replacementStarted = false;
        try
        {
            WriteProgress(options, $"正在下载 {update.PackageAsset.Name}...");
            await releaseClient.DownloadVerifiedPackageAsync(update, packagePath);
            WriteProgress(options, "安装包 SHA-256 校验通过，正在解压到暂存目录...");
            SecureZipExtractor extractor = new();
            extractor.ExtractReleasePackage(packagePath, stagingRoot);
            packageValidator.Validate(stagingRoot, update.Manifest.Version);

            ProcessWaiter processWaiter = new();
            await processWaiter.WaitForExitAsync(
                options.WaitProcessIds,
                TimeSpan.FromSeconds(options.WaitTimeoutSeconds),
                message => WriteProgress(options, message));

            using UpdateTargetLock targetLock = UpdateTargetLock.Acquire(
                options.TargetRoot,
                TimeSpan.FromSeconds(30));
            string recovery = installer.RecoverIncomplete(options.TargetRoot);
            if (!string.IsNullOrWhiteSpace(recovery)) WriteProgress(options, recovery);
            replacementStarted = true;
            string backup = installer.Apply(stagingRoot, options.TargetRoot, update.Manifest.Version);
            UpdaterResult result = new()
            {
                Success = true,
                UpdateAvailable = false,
                CurrentVersion = options.CurrentVersion,
                LatestVersion = update.Manifest.Version,
                Message = $"AutoPlayer 已更新到 {update.Manifest.Version}。",
                BackupDirectory = backup
            };
            if (options.RestartManager)
            {
                try
                {
                    ManagerRestarter restarter = new();
                    restarter.Restart(options.TargetRoot);
                }
                catch (Exception exception)
                {
                    result.Message += " 但 Manager 重启失败：" + GetUserFacingFailureMessage(exception);
                }
            }

            WriteResult(result, options.JsonOutput);
            return 0;
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, Path.Combine(Path.GetTempPath(), "LoopstructorAutoPlayerUpdater"));
            if (!replacementStarted)
            {
                string targetParent = Directory.GetParent(ReleasePackageValidator.NormalizeRoot(options.TargetRoot))!.FullName;
                DeleteTemporaryDirectory(stagingRoot, targetParent, ".LoopstructorAutoPlayer-staging-");
            }
        }
    }

    private static void WriteResult(UpdaterResult result, bool json)
    {
        if (json)
        {
            Console.Out.Write(JsonSerializer.Serialize(result, OutputJson));
            return;
        }

        Console.WriteLine(result.Success ? result.Message : "更新失败：" + result.Message);
        if (!string.IsNullOrWhiteSpace(result.BackupDirectory))
        {
            Console.WriteLine("上一版本备份：" + result.BackupDirectory);
        }
    }

    private static void WriteProgress(UpdateCommandOptions options, string message)
    {
        if (!options.JsonOutput) Console.WriteLine(message);
    }

    internal static string GetUserFacingFailureMessage(Exception exception)
    {
        string message = exception.Message?.Trim() ?? string.Empty;
        if (IsUpdaterAuthored(exception) && ContainsChineseText(message))
        {
            return message;
        }

        return exception switch
        {
            OperationCanceledException => "更新请求已取消或等待超时。",
            HttpRequestException httpException when httpException.StatusCode.HasValue =>
                $"无法访问 GitHub（HTTP {(int)httpException.StatusCode.Value}），请稍后重试并检查网络、代理或防火墙设置。",
            HttpRequestException => "无法连接 GitHub，请检查网络、代理或防火墙设置后重试。",
            UnauthorizedAccessException => "更新器没有访问目标文件或目录的权限，请检查目录权限后重试。",
            JsonException => "更新数据格式无效，无法继续更新。",
            IOException => "更新文件读写失败，请关闭正在占用文件的程序后重试。",
            _ => "更新器遇到未预期错误，请重新下载完整发布包后重试。"
        };
    }

    private static bool IsUpdaterAuthored(Exception exception) =>
        exception.TargetSite?.DeclaringType?.Assembly == typeof(Program).Assembly;

    private static bool ContainsChineseText(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static void DeleteTemporaryDirectory(string path, string allowedParent, string? requiredPrefix = null)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            string parent = Path.GetFullPath(allowedParent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            string prefix = parent + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(requiredPrefix)
                    && !Path.GetFileName(full).StartsWith(requiredPrefix, StringComparison.Ordinal)))
            {
                return;
            }

            Directory.Delete(full, recursive: true);
        }
        catch
        {
            // Temporary download and pre-transaction staging cleanup is best effort.
        }
    }
}
