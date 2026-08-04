using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;
using Loopstructor.AutoPlayer.Updater.UI;

namespace Loopstructor.AutoPlayer.Updater;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [STAThread]
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
                using Process stagedProcess = relocator.RelaunchFromTemporaryCopy(
                    args,
                    redirectOutput: options.JsonOutput);
                if (!options.JsonOutput)
                {
                    return 0;
                }

                Task<string> outputTask = stagedProcess.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = stagedProcess.StandardError.ReadToEndAsync();
                await stagedProcess.WaitForExitAsync();
                Console.Out.Write(await outputTask);
                Console.Error.Write(await errorTask);
                return stagedProcess.ExitCode;
            }

            if (options.Command == UpdateCommand.Apply && !options.JsonOutput)
            {
                return options.DemoUi ? RunDemoUi(options) : RunApplyUi(options);
            }

            UpdaterResult result = await ExecuteAsync(options, progress: null, CancellationToken.None);
            WriteResult(result, options.JsonOutput);
            return result.Success ? 0 : 1;
        }
        catch (Exception exception)
        {
            UpdaterResult failure = Failure(options?.CurrentVersion ?? string.Empty, exception);
            if (options?.Command == UpdateCommand.Apply && options.RestartManager)
            {
                TryRestartManagerAfterFailure(options.TargetRoot, failure);
            }

            if (options?.Command == UpdateCommand.Apply && !(options.JsonOutput || wantsJson))
            {
                ShowStartupFailure(failure);
            }
            else
            {
                WriteResult(failure, options?.JsonOutput ?? wantsJson);
            }

            return 1;
        }
    }

    private static int RunApplyUi(UpdateCommandOptions options) => RunOnStaThread(() =>
    {
        System.Windows.Application application = new()
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose
        };
        UpdateForm form = new(
            options.CurrentVersion,
            (progress, cancellationToken, tryBeginCommit) =>
                ExecuteAsync(options, progress, cancellationToken, tryBeginCommit));
        application.Run(form);
        return form.ExitCode;
    });

    private static int RunDemoUi(UpdateCommandOptions options) => RunOnStaThread(() =>
    {
        System.Windows.Application application = new()
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose
        };
        UpdateForm form = UpdateForm.CreateDemo(
            options.CurrentVersion,
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? options.CurrentVersion);
        application.Run(form);
        return 0;
    });

    private static int RunOnStaThread(Func<int> operation)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return operation();
        }

        int exitCode = 1;
        Exception? failure = null;
        Thread uiThread = new(() =>
        {
            try
            {
                exitCode = operation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = false,
            Name = "AutoPlayer Updater UI"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return exitCode;
    }

    internal static async Task<UpdaterResult> ExecuteAsync(
        UpdateCommandOptions options,
        IProgress<UpdateProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        Func<bool>? tryBeginCommit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ProgressContext progressContext = new(progress);
        try
        {
            progressContext.Report(
                UpdateProgressStage.Preparing,
                1,
                "正在读取更新配置...",
                canCancel: true);
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
            progressContext.Report(
                UpdateProgressStage.Checking,
                4,
                "正在查询 GitHub 最新版本...",
                canCancel: true);
            ResolvedUpdate update = await releaseClient.ResolveLatestAsync(cancellationToken);
            if (!SemanticVersion.TryParse(options.CurrentVersion, out SemanticVersion? current)
                || !SemanticVersion.TryParse(update.Manifest.Version, out SemanticVersion? latest))
            {
                throw new InvalidDataException("当前版本或最新发布版本无效。");
            }

            bool updateAvailable = latest!.CompareTo(current) > 0;
            if (options.Command == UpdateCommand.Check)
            {
                return new UpdaterResult
                {
                    Success = true,
                    UpdateAvailable = updateAvailable,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = updateAvailable
                        ? $"发现 AutoPlayer {update.Manifest.Version} 新版本。"
                        : "AutoPlayer 已是最新版本。"
                };
            }

            if (!updateAvailable)
            {
                UpdaterResult currentResult = new()
                {
                    Success = true,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = "无需更新。"
                };
                if (options.Command == UpdateCommand.Apply && options.RestartManager)
                {
                    try
                    {
                        new ManagerRestarter().Restart(options.TargetRoot);
                    }
                    catch (Exception restartException)
                    {
                        currentResult.ManagerRestartFailed = true;
                        currentResult.Message += " 但 Manager 重启失败：" +
                                                 GetUserFacingFailureMessage(restartException);
                    }
                }

                progressContext.Report(
                    UpdateProgressStage.Completed,
                    100,
                    currentResult.Message,
                    canCancel: false);
                return currentResult;
            }

            return await ApplyAsync(
                options,
                update,
                releaseClient,
                progressContext,
                cancellationToken,
                tryBeginCommit);
        }
        catch (Exception exception)
        {
            UpdaterResult failure = Failure(
                options.CurrentVersion,
                exception,
                cancellationToken.IsCancellationRequested);
            if (options.Command == UpdateCommand.Apply && options.RestartManager)
            {
                TryRestartManagerAfterFailure(options.TargetRoot, failure);
            }

            progressContext.ReportFailure(failure.Message);
            return failure;
        }
    }

    private static async Task<UpdaterResult> ApplyAsync(
        UpdateCommandOptions options,
        ResolvedUpdate update,
        GitHubReleaseClient releaseClient,
        ProgressContext progress,
        CancellationToken cancellationToken,
        Func<bool>? tryBeginCommit)
    {
        ReleasePackageValidator packageValidator = new();
        progress.Report(
            UpdateProgressStage.Preparing,
            7,
            "正在验证当前安装目录...",
            canCancel: true);
        ReleaseMarker installedMarker = await Task.Run(
            () => packageValidator.Validate(options.TargetRoot, validateTargetSafety: true),
            cancellationToken);
        ResolvedDeltaPackage? selectedDelta = SelectDeltaPackage(update, installedMarker.Version);
        GitHubReleaseAsset selectedAsset = selectedDelta?.PackageAsset ?? update.PackageAsset;
        long selectedSize = selectedDelta?.Manifest.Size ?? update.Manifest.Size;
        string packageKind = selectedDelta is null ? "完整安装包" : "增量更新包";
        TransactionalInstaller installer = new(
            packageValidator,
            TransactionalInstaller.GetDefaultJournalPath(options.TargetRoot));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerUpdater",
            "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string packagePath = Path.Combine(temporaryRoot, selectedAsset.Name);
        string deltaExtractionRoot = Path.Combine(temporaryRoot, "delta");
        string stagingRoot = installer.CreateStagingRoot(options.TargetRoot);
        bool replacementStarted = false;
        try
        {
            progress.Report(
                UpdateProgressStage.Downloading,
                10,
                $"正在下载{packageKind} {selectedAsset.Name}...",
                canCancel: true,
                downloadedBytes: 0,
                totalBytes: selectedSize);
            IProgress<PackageDownloadProgress> downloadProgress = new CallbackProgress<PackageDownloadProgress>(value =>
                progress.Report(
                    UpdateProgressStage.Downloading,
                    UpdateProgressMath.DownloadOverallPercent(value.DownloadedBytes, value.TotalBytes),
                    $"正在下载{packageKind} {selectedAsset.Name}...",
                    canCancel: true,
                    downloadedBytes: value.DownloadedBytes,
                    totalBytes: value.TotalBytes,
                    bytesPerSecond: value.BytesPerSecond));
            if (selectedDelta is null)
            {
                await releaseClient.DownloadVerifiedPackageAsync(
                    update,
                    packagePath,
                    downloadProgress,
                    cancellationToken);
            }
            else
            {
                try
                {
                    await releaseClient.DownloadVerifiedDeltaPackageAsync(
                        update,
                        selectedDelta,
                        packagePath,
                        downloadProgress,
                        cancellationToken);
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    selectedDelta = null;
                    selectedAsset = update.PackageAsset;
                    selectedSize = update.Manifest.Size;
                    packageKind = "完整安装包";
                    packagePath = Path.Combine(temporaryRoot, selectedAsset.Name);
                    progress.Report(
                        UpdateProgressStage.Downloading,
                        10,
                        "增量更新包不存在，正在改用完整安装包...",
                        canCancel: true,
                        downloadedBytes: 0,
                        totalBytes: selectedSize);
                    await releaseClient.DownloadVerifiedPackageAsync(
                        update,
                        packagePath,
                        downloadProgress,
                        cancellationToken);
                }
            }

            progress.Report(
                UpdateProgressStage.Verifying,
                64,
                $"下载完成，{packageKind} SHA-256 校验通过。",
                canCancel: true,
                downloadedBytes: selectedSize,
                totalBytes: selectedSize);
            SecureZipExtractor extractor = new();
            IProgress<ArchiveExtractionProgress> extractionProgress = new CallbackProgress<ArchiveExtractionProgress>(value =>
                progress.Report(
                    UpdateProgressStage.Extracting,
                    UpdateProgressMath.ExtractionOverallPercent(value.ExtractedBytes, value.TotalBytes),
                    selectedDelta is null
                        ? $"正在解压安装文件（{value.ExtractedFiles}/{value.TotalFiles}）..."
                        : $"正在重建增量更新文件（{value.ExtractedFiles}/{value.TotalFiles}）...",
                    canCancel: true));
            if (selectedDelta is null)
            {
                await Task.Run(
                    () => extractor.ExtractReleasePackage(
                        packagePath,
                        stagingRoot,
                        extractionProgress,
                        cancellationToken),
                    cancellationToken);
            }
            else
            {
                await Task.Run(
                    () => extractor.ExtractDeltaPackage(
                        packagePath,
                        deltaExtractionRoot,
                        progress: null,
                        cancellationToken),
                    cancellationToken);
                DeltaPackageReconstructor reconstructor = new(packageValidator);
                await Task.Run(
                    () => reconstructor.Reconstruct(
                        deltaExtractionRoot,
                        options.TargetRoot,
                        stagingRoot,
                        selectedDelta.Manifest.FromVersion,
                        update.Manifest.Version,
                        extractionProgress,
                        cancellationToken),
                    cancellationToken);
            }

            progress.Report(
                UpdateProgressStage.Verifying,
                85,
                "正在校验解压后的发布文件...",
                canCancel: true);
            await Task.Run(
                () => packageValidator.Validate(stagingRoot, update.Manifest.Version),
                cancellationToken);

            progress.Report(
                UpdateProgressStage.WaitingForProcesses,
                88,
                "正在等待 Manager 和游戏进程退出...",
                canCancel: true);
            ProcessWaiter processWaiter = new();
            await processWaiter.WaitForExitAsync(
                options.WaitProcessIds,
                TimeSpan.FromSeconds(options.WaitTimeoutSeconds),
                message => progress.Report(
                    UpdateProgressStage.WaitingForProcesses,
                    88,
                    message,
                    canCancel: true),
                cancellationToken);

            if (tryBeginCommit is not null && !tryBeginCommit())
            {
                throw new OperationCanceledException(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(
                UpdateProgressStage.Installing,
                90,
                "正在锁定更新目录，请勿关闭窗口...",
                canCancel: false);
            using UpdateTargetLock targetLock = await Task.Run(
                () => UpdateTargetLock.Acquire(options.TargetRoot, TimeSpan.FromSeconds(30)),
                CancellationToken.None);
            string recovery = await Task.Run(
                () => installer.RecoverIncomplete(options.TargetRoot),
                CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(recovery))
            {
                progress.Report(UpdateProgressStage.Installing, 92, recovery, canCancel: false);
            }

            if (selectedDelta is not null)
            {
                ReleaseMarker commitMarker = await Task.Run(
                    () => packageValidator.Validate(
                        options.TargetRoot,
                        selectedDelta.Manifest.FromVersion,
                        validateTargetSafety: true),
                    CancellationToken.None);
                if (!string.Equals(
                        commitMarker.Version,
                        selectedDelta.Manifest.FromVersion,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("提交增量更新前，当前安装版本已经发生变化。");
                }
            }

            replacementStarted = true;
            string backup = await Task.Run(
                () => installer.Apply(
                    stagingRoot,
                    options.TargetRoot,
                    update.Manifest.Version,
                    phase => ReportInstallPhase(progress, phase)),
                CancellationToken.None);
            UpdaterResult result = new()
            {
                Success = true,
                UpdateAvailable = false,
                CurrentVersion = options.CurrentVersion,
                LatestVersion = update.Manifest.Version,
                Message = selectedDelta is null
                    ? $"AutoPlayer 已通过完整安装包更新到 {update.Manifest.Version}。"
                    : $"AutoPlayer 已通过增量更新包更新到 {update.Manifest.Version}。",
                BackupDirectory = backup,
                UsedIncrementalUpdate = selectedDelta is not null
            };
            if (options.RestartManager)
            {
                progress.Report(
                    UpdateProgressStage.Restarting,
                    99,
                    "正在重新启动 Manager...",
                    canCancel: false);
                try
                {
                    ManagerRestarter restarter = new();
                    restarter.Restart(options.TargetRoot);
                }
                catch (Exception exception)
                {
                    result.ManagerRestartFailed = true;
                    result.Message += " 但 Manager 重启失败：" + GetUserFacingFailureMessage(exception);
                }
            }

            progress.Report(
                UpdateProgressStage.Completed,
                100,
                result.Message,
                canCancel: false);
            return result;
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

    private static void ReportInstallPhase(ProgressContext progress, UpdateInstallPhase phase)
    {
        (int percent, string message) = phase switch
        {
            UpdateInstallPhase.Prepared => (92, "更新事务已准备完成。"),
            UpdateInstallPhase.BackupCreated => (94, "当前版本已安全备份。"),
            UpdateInstallPhase.Installed => (97, "新版文件已完成替换。"),
            UpdateInstallPhase.Validated => (98, "新版文件校验通过。"),
            _ => (92, "正在安装更新...")
        };
        progress.Report(UpdateProgressStage.Installing, percent, message, canCancel: false);
    }

    internal static ResolvedDeltaPackage? SelectDeltaPackage(
        ResolvedUpdate update,
        string installedVersion)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(installedVersion)) return null;
        return update.DeltaPackages.SingleOrDefault(delta =>
            string.Equals(delta.Manifest.FromVersion, installedVersion, StringComparison.Ordinal)
            && delta.Manifest.Size < update.Manifest.Size);
    }

    private static UpdaterResult Failure(
        string currentVersion,
        Exception exception,
        bool cancellationRequested = false) =>
        new()
        {
            CurrentVersion = currentVersion,
            Message = GetUserFacingFailureMessage(exception, cancellationRequested)
        };

    private static void TryRestartManagerAfterFailure(string targetRoot, UpdaterResult result)
    {
        try
        {
            new ManagerRestarter().Restart(targetRoot);
        }
        catch (Exception restartException)
        {
            result.ManagerRestartFailed = true;
            result.Message += " Manager 也未能重新启动：" + GetUserFacingFailureMessage(restartException);
        }
    }

    private static void ShowStartupFailure(UpdaterResult failure)
    {
        try
        {
            System.Windows.MessageBox.Show(
                failure.Message,
                "Loopstructor 2.AutoPlayer 更新器启动失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            WriteResult(failure, json: false);
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

    internal static string GetUserFacingFailureMessage(
        Exception exception,
        bool cancellationRequested = false)
    {
        string message = exception.Message?.Trim() ?? string.Empty;
        if (IsUpdaterAuthored(exception) && ContainsChineseText(message))
        {
            return message;
        }

        return exception switch
        {
            OperationCanceledException when cancellationRequested => "更新已取消。",
            OperationCanceledException => "连接 GitHub 超时，请检查网络、代理或防火墙设置后重试。",
            TimeoutException => "等待 Manager 或游戏进程退出时超时。",
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

    private sealed class ProgressContext
    {
        private readonly IProgress<UpdateProgressSnapshot>? _progress;

        public ProgressContext(IProgress<UpdateProgressSnapshot>? progress)
        {
            _progress = progress;
            Current = new UpdateProgressSnapshot
            {
                Stage = UpdateProgressStage.Preparing,
                OverallPercent = 0,
                Message = "正在准备更新...",
                CanCancel = true
            };
        }

        public UpdateProgressSnapshot Current { get; private set; }

        public void Report(
            UpdateProgressStage stage,
            int overallPercent,
            string message,
            bool canCancel,
            long? downloadedBytes = null,
            long? totalBytes = null,
            double bytesPerSecond = 0)
        {
            Current = new UpdateProgressSnapshot
            {
                Stage = stage,
                OverallPercent = Math.Max(Current.OverallPercent, Math.Clamp(overallPercent, 0, 100)),
                Message = message,
                DownloadedBytes = downloadedBytes ?? Current.DownloadedBytes,
                TotalBytes = totalBytes ?? Current.TotalBytes,
                BytesPerSecond = bytesPerSecond,
                CanCancel = canCancel
            };
            ReportSafely(Current);
        }

        public void ReportFailure(string message)
        {
            Current = new UpdateProgressSnapshot
            {
                Stage = Current.Stage,
                OverallPercent = Current.OverallPercent,
                Message = message,
                DownloadedBytes = Current.DownloadedBytes,
                TotalBytes = Current.TotalBytes,
                CanCancel = false,
                IsFailure = true
            };
            ReportSafely(Current);
        }

        private void ReportSafely(UpdateProgressSnapshot value)
        {
            try
            {
                _progress?.Report(value);
            }
            catch
            {
                // Progress display is non-authoritative and must never interrupt an update.
            }
        }
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public CallbackProgress(Action<T> callback)
        {
            _callback = callback;
        }

        public void Report(T value) => _callback(value);
    }
}
