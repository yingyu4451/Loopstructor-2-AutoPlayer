using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Host;

internal sealed class DesktopHostEngine : IAsyncDisposable
{
    private static readonly JsonSerializer CamelSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) }
    });
    private const int MaximumLogEntries = 600;
    private readonly Func<string, object?, Task> _emit;
    private readonly CancellationToken _lifetime;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ManagerSettingsStore _settingsStore;
    private readonly GameInstallValidator _validator = new();
    private readonly BepInExConfigWriter _configWriter = new();
    private readonly InstalledControlSessionStore _installedSessions;
    private readonly PipeControlClient _pipeClient = new();
    private readonly LogTailReader _logTail = new();
    private readonly GameUpdateShutdownCoordinator _gameUpdateShutdown = new();
    private readonly List<HostLogEntry> _logs = new();
    private readonly DistributionLayout _distribution;
    private readonly BepInExInstaller _installer;
    private readonly GameLauncher _gameLauncher;
    private readonly UpdateCoordinator _updates;
    private readonly SaveBackupService _saveBackups;
    private readonly CancellationTokenSource _pollLifetime;
    private ManagerSettings _settings;
    private GameInstallValidation? _game;
    private PluginInstallStatus? _pluginStatus;
    private ActivationSession? _session;
    private BridgeHello? _hello;
    private AutoPlayerStatus? _status;
    private ManagerUpdateStatus? _updateStatus;
    private bool _trusted;
    private bool _pollConnectedLastTime;
    private DateTime _nextAutomaticCheatEnableUtc;
    private string _lastAutomaticCheatEnableError = string.Empty;
    private string _connectionLabel = "等待游戏连接";
    private string _connectionReason = string.Empty;
    private Task? _pollTask;

    public DesktopHostEngine(Func<string, object?, Task> emit, CancellationToken lifetime)
    {
        _emit = emit;
        _lifetime = lifetime;
        string dataRoot = ResolveDataRoot();
        _settingsStore = new ManagerSettingsStore(Path.Combine(dataRoot, "manager", "settings.json"));
        _installedSessions = new InstalledControlSessionStore(dataRoot);
        _pollLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _settings = _settingsStore.Load(out string warning);
        _distribution = DistributionLayout.Locate();
        _installer = new BepInExInstaller(_distribution, _configWriter);
        _gameLauncher = new GameLauncher(new ActivationSessionFactory(), _configWriter);
        _updates = new UpdateCoordinator(_distribution);
        _saveBackups = new SaveBackupService(Path.Combine(dataRoot, "save-backups"));
        _saveBackups.EnsureBackupRoot(_settings.GameRoot);
        if (!string.IsNullOrWhiteSpace(warning)) AddLog("warn", warning);
    }

    private static string ResolveDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("LOOPSTRUCTOR_AUTOPLAYER_HOST_DATA_ROOT");
        return string.IsNullOrWhiteSpace(overrideRoot)
            ? Protocol.DataRoot
            : Path.GetFullPath(overrideRoot);
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.GameRoot))
        {
            try
            {
                await ValidateGameCoreAsync(_settings.GameRoot);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                AddLog("warn", "上次选择的游戏目录当前不可用，请重新选择：" + exception.Message);
            }
        }

        await EmitSnapshotAsync();
        _pollTask = PollLoopAsync(_pollLifetime.Token);
        _ = CheckUpdatesCoreAsync(announce: false);
    }

    public async Task<JToken?> ExecuteAsync(string method, JObject? parameters)
    {
        await _operationGate.WaitAsync(_lifetime);
        try
        {
            return method switch
            {
                "app.getSnapshot" => Serialize(BuildSnapshot()),
                "settings.save" => await SaveSettingsAsync(parameters),
                "game.validate" => await ValidateGameAsync(RequiredString(parameters, "path")),
                "plugin.install" => await InstallPluginAsync(),
                "plugin.setEnabled" => SetPluginEnabled(parameters?.Value<bool?>("enabled") == true),
                "plugin.uninstall" => UninstallPlugin(),
                "game.launch" => LaunchGame(),
                "connection.refresh" => await RefreshConnectionAsync(),
                "cheat.command" => await SendCheatAsync(parameters),
                "automation.querySetup" => await QueryAutomationSetupAsync(),
                "automation.start" => await StartAutomationAsync(),
                "automation.pause" => await SendAutomationControlAsync("pause", null),
                "automation.resume" => await SendAutomationControlAsync("resume", null),
                "automation.stop" => await StopAutomationAsync(),
                "update.check" => await CheckUpdatesAsync(),
                "update.inspectProcesses" => InspectUpdateProcesses(),
                "update.closeGame" => await CloseGameForUpdateAsync(),
                "update.apply" => StartUpdate(parameters?.Value<int?>("desktopProcessId") ?? 0),
                "diagnostics.openEvidence" => OpenEvidenceDirectory(),
                "backups.open" => OpenSaveBackupDirectory(),
                "backups.list" => ListSaveBackups(),
                "backups.restore" => await RestoreSaveBackupAsync(RequiredString(parameters, "backupId")),
                "logs.clear" => ClearLogs(),
                _ => throw new InvalidOperationException("Host 不允许调用未知方法：" + method)
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<JToken> SaveSettingsAsync(JObject? parameters)
    {
        ManagerSettings incoming = parameters?.ToObject<ManagerSettings>()
                                   ?? throw new InvalidOperationException("设置内容为空。");
        incoming.GameRoot = _settings.GameRoot;
        incoming.GitHubOwner = _settings.GitHubOwner;
        incoming.GitHubRepository = _settings.GitHubRepository;
        incoming.ProfileName = string.IsNullOrWhiteSpace(incoming.ProfileName)
            ? "player-default"
            : incoming.ProfileName.Trim();
        if (!Enum.IsDefined(incoming.GameMode)) incoming.GameMode = AutomationGameMode.Common;
        if (!Enum.IsDefined(incoming.DecisionPriority))
            incoming.DecisionPriority = AutomationDecisionPriority.CatapultPoints;
        incoming.SpeedState = Math.Clamp(incoming.SpeedState, 0, 2);
        incoming.MaxRunMinutes = Math.Clamp(incoming.MaxRunMinutes, 5, 480);
        incoming.CharacterCfgIndex = Math.Max(-1, incoming.CharacterCfgIndex);
        incoming.NormalizeUpdateSource();
        _settings = incoming;
        _settingsStore.Save(_settings);
        await ObserveSaveBackupsAsync(null);
        await EmitSnapshotAsync();
        return Serialize(_settings);
    }

    private async Task<JToken> ValidateGameAsync(string root)
    {
        await ValidateGameCoreAsync(root);
        await EmitSnapshotAsync();
        return Serialize(new { validation = _game, plugin = _pluginStatus });
    }

    private async Task ValidateGameCoreAsync(string root)
    {
        GameInstallValidation validation = await _validator.ValidateAsync(root, _lifetime);
        if (!validation.IsValid)
        {
            _game = null;
            _pluginStatus = null;
            ResetSession();
            string message = validation.Errors.FirstOrDefault() ?? "游戏构建验证失败。";
            AddLog("error", message);
            throw new InvalidOperationException(message);
        }

        _game = validation;
        _settings.GameRoot = validation.GameRoot;
        _settingsStore.Save(_settings);
        RefreshPluginStatus();
        AddLog("safe", $"已验证 Skyspine {Display(validation.ProductVersion)} / {ShortHash(validation.AssemblySha256)}。");
        foreach (string warning in validation.Warnings) AddLog("warn", warning);
        if (_pluginStatus?.State == PluginState.Enabled) PrepareInstalledSession(selectProfile: false);
    }

    private async Task<JToken> InstallPluginAsync()
    {
        GameInstallValidation game = RequireGame();
        PluginOperationResult result = await _installer.InstallAsync(game, _lifetime);
        RefreshPluginStatus();
        if (result.Success) PrepareInstalledSession(selectProfile: true);
        AddLog(result.Success ? "success" : "error", result.Message);
        await EmitSnapshotAsync();
        return Serialize(result);
    }

    private JToken SetPluginEnabled(bool enabled)
    {
        GameInstallValidation game = RequireGame();
        PluginOperationResult result = _installer.SetEnabled(game.GameRoot, enabled);
        RefreshPluginStatus();
        if (result.Success && enabled) PrepareInstalledSession(selectProfile: false);
        if (result.Success && !enabled) ResetSession();
        AddLog(result.Success ? "success" : "error", result.Message);
        _ = EmitSnapshotAsync();
        return Serialize(result);
    }

    private JToken UninstallPlugin()
    {
        GameInstallValidation game = RequireGame();
        PluginOperationResult result = _installer.Uninstall(game.GameRoot);
        if (result.Success)
        {
            _installedSessions.Delete(game.GameRoot);
            ResetSession();
        }
        RefreshPluginStatus();
        AddLog(result.Success ? "success" : "error", result.Message);
        _ = EmitSnapshotAsync();
        return Serialize(result);
    }

    private JToken LaunchGame()
    {
        GameInstallValidation game = RequireGame();
        if (_pluginStatus?.State != PluginState.Enabled)
            throw new InvalidOperationException("启动前必须安装并启用插件。");

        IReadOnlyList<int> running = FindRunningGameProcesses(game.ExecutablePath);
        if (running.Count > 1)
            throw new InvalidOperationException("检测到多个相同目录的 Skyspine 游戏进程，请只保留一个。");
        if (running.Count == 1)
        {
            PrepareInstalledSession(selectProfile: false);
            BindProcess(running[0]);
            AddLog("info", $"游戏已运行（PID {running[0]}），正在连接现有进程。");
            _ = EmitSnapshotAsync();
            return Serialize(new { success = true, processId = running[0], attached = true });
        }

        ActivationSession installed = _installedSessions.Ensure(game, _settings.ProfileName, selectProfile: true);
        GameLaunchResult result = _gameLauncher.Launch(game, installed);
        if (!result.Success || result.Session == null)
            throw new InvalidOperationException(result.Message);
        AdoptSession(result.Session, includeExistingLog: true);
        _connectionLabel = "正在等待插件握手";
        AddLog("info", result.Message);
        _ = EmitSnapshotAsync();
        return Serialize(new { success = true, processId = result.Session.ProcessId, attached = false });
    }

    private async Task<JToken> RefreshConnectionAsync()
    {
        await PollOnceAsync();
        await EmitSnapshotAsync();
        return Serialize(BuildSnapshot());
    }

    private async Task<JToken> SendCheatAsync(JObject? parameters)
    {
        string command = RequiredString(parameters, "command");
        if (!CheatCommands.All.Contains(command, StringComparer.Ordinal))
            throw new InvalidOperationException("该作弊命令不在 Host 白名单中。");
        if (!_trusted || _session == null)
            throw new InvalidOperationException("尚未与当前游戏建立安全连接。");

        JObject? arguments = parameters?["arguments"] as JObject;
        PipeCallResult call = await _pipeClient.SendCheatAsync(_session, command, arguments, _lifetime);
        if (!call.TransportSuccess)
        {
            InvalidateTrust();
            bool unknown = call.RequestMayHaveExecuted && CheatCommands.IsMutationCommand(command);
            string error = unknown
                ? "命令可能已经执行，但结果尚未确认；为避免重复写入，请重新连接后先读取状态。"
                : call.Error;
            AddLog("error", error);
            throw new InvalidOperationException(error);
        }

        ControlResponse response = call.Response!;
        if (response.Status != null) _status = response.Status;
        if (response.Data != null) InlineVerifiedCatalogIcons(response.Data);
        if (!response.Success || !string.Equals(command, CheatCommands.QueryState, StringComparison.Ordinal))
            AddLog(response.Success ? "cheat" : "error", response.Message);
        await EmitSnapshotAsync();
        return Serialize(response);
    }

    private async Task<JToken> StopAutomationAsync()
    {
        return await SendAutomationControlAsync("stop", null);
    }

    private async Task<JToken> QueryAutomationSetupAsync()
    {
        ControlResponse response = await QueryAutomationSetupCoreAsync();
        return Serialize(response);
    }

    private async Task<ControlResponse> QueryAutomationSetupCoreAsync()
    {
        if (!_trusted || _session == null)
            throw new InvalidOperationException("连接游戏后才能读取可玩的模式和角色。");
        PipeCallResult call = await _pipeClient.QueryAutomationSetupAsync(_session, _lifetime);
        if (!call.TransportSuccess)
        {
            InvalidateTrust();
            throw new InvalidOperationException(call.Error);
        }
        ControlResponse response = call.Response
                                   ?? throw new InvalidOperationException("插件没有返回自动游玩配置。");
        if (!response.Success || response.Data == null)
            throw new InvalidOperationException(response.Message ?? "当前无法读取自动游玩配置。");
        if (response.Status != null) _status = response.Status;
        return response;
    }

    private async Task<JToken> StartAutomationAsync()
    {
        RunControlAvailability availability = RunControlAvailability.From(_trusted, _status);
        if (!availability.CanStart)
            throw new InvalidOperationException(BuildUnavailableAutomationMessage("开始"));

        AutomationRunOptions options = await BuildAutomationRunOptionsAsync();
        return await SendAutomationControlAsync("start", options);
    }

    private async Task<AutomationRunOptions> BuildAutomationRunOptionsAsync()
    {
        AutomationRunOptions options = new()
        {
            Mode = _settings.GameMode,
            GameSpeedControlVersion = AutoPlayerGameSpeed.CurrentOptionsVersion,
            OverrideGameSpeed = _settings.OverrideGameSpeed,
            SpeedState = Math.Clamp(_settings.SpeedState, 0, 2),
            MaxRunMinutes = Math.Clamp(_settings.MaxRunMinutes, 5, 480),
            ContinueExistingProfile = _settings.ContinueExistingProfile,
            SkipStory = _settings.SkipStory,
            DecisionPriority = Enum.IsDefined(_settings.DecisionPriority)
                ? _settings.DecisionPriority
                : AutomationDecisionPriority.CatapultPoints
        };
        if (_settings.ContinueExistingProfile) return options;

        ControlResponse setup = await QueryAutomationSetupCoreAsync();
        JArray modes = setup.Data?["modes"] as JArray ?? new JArray();
        JObject? selectedMode = modes.OfType<JObject>().FirstOrDefault(item =>
            ParseAutomationMode(item.Value<string>("mode")) == _settings.GameMode);
        if (selectedMode?.Value<bool?>("available") != true)
        {
            string reason = selectedMode?.Value<string>("reason") ?? "当前游戏没有提供该模式的可玩入口。";
            throw new InvalidOperationException(reason);
        }

        if (_settings.GameMode == AutomationGameMode.Common)
        {
            JObject[] characters = (setup.Data?["characters"] as JArray)?.OfType<JObject>().ToArray()
                                   ?? Array.Empty<JObject>();
            JObject? character = characters.FirstOrDefault(item =>
                                     item.Value<int?>("cfgIndex") == _settings.CharacterCfgIndex)
                                 ?? characters.FirstOrDefault();
            if (character == null)
                throw new InvalidOperationException("当前游戏没有已解锁且可开始新游戏的角色。");
            options.CharacterIndex = character.Value<int?>("runtimeIndex") ?? 0;
            options.DifficultyIndex = character.Value<int?>("difficultyIndex") ?? 0;
            options.SuperModuleIndex = character.Value<int?>("superModuleIndex") ?? 0;
            int cfgIndex = character.Value<int?>("cfgIndex") ?? -1;
            if (_settings.CharacterCfgIndex != cfgIndex)
            {
                _settings.CharacterCfgIndex = cfgIndex;
                _settingsStore.Save(_settings);
            }
        }
        return options;
    }

    private async Task<JToken> SendAutomationControlAsync(string command, AutomationRunOptions? options)
    {
        if (!_trusted || _session == null)
            throw new InvalidOperationException("尚未与当前游戏建立安全连接。");

        RunControlAvailability availability = RunControlAvailability.From(true, _status);
        bool allowed = command switch
        {
            "start" => availability.CanStart,
            "pause" => availability.CanPause,
            "resume" => availability.CanResume,
            "stop" => availability.CanStop,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException(BuildUnavailableAutomationMessage(command));

        PipeCallResult call = command switch
        {
            "start" => await _pipeClient.StartAsync(_session, options!, _lifetime),
            "pause" => await _pipeClient.PauseAsync(_session, _lifetime),
            "resume" => await _pipeClient.ResumeAsync(_session, _lifetime),
            "stop" => await _pipeClient.StopAsync(_session, _lifetime),
            _ => throw new InvalidOperationException("不支持的自动游玩命令。")
        };
        if (!call.TransportSuccess) throw new InvalidOperationException(call.Error);
        if (call.Response?.Status != null) _status = call.Response.Status;
        AddLog(call.Response?.Success == true ? "success" : "error", call.Response?.Message ?? "自动游玩命令没有返回结果。");
        await EmitSnapshotAsync();
        return Serialize(call.Response ?? new ControlResponse { Success = false, Message = "插件没有返回响应。" });
    }

    private string BuildUnavailableAutomationMessage(string command)
    {
        if (!_trusted) return "尚未与当前游戏建立安全连接。";
        if (_status?.NeedsProcessRestart == true) return "当前游戏进程需要重启后才能开始新的自动游玩。";
        if (_status?.BaseGodModeEnabled == true || _status?.MapSkipEnabled == true)
            return "请先关闭基地无敌和地图节点自由跳转，再开始自动游玩。";
        return $"当前运行状态不允许{command}自动游玩。";
    }

    private static AutomationGameMode ParseAutomationMode(string? value) =>
        string.Equals(value, "random", StringComparison.OrdinalIgnoreCase)
            ? AutomationGameMode.Random
            : AutomationGameMode.Common;

    private async Task<JToken> CheckUpdatesAsync()
    {
        await CheckUpdatesCoreAsync(announce: true);
        return Serialize(_updateStatus!);
    }

    private async Task CheckUpdatesCoreAsync(bool announce)
    {
        ManagerUpdateStatus status = await _updates.CheckAsync(_settings, _lifetime);
        _updateStatus = status;
        if (announce || status.UpdateAvailable) AddLog(status.Success ? "info" : "warn", status.Message);
        await EmitSnapshotAsync();
    }

    private JToken InspectUpdateProcesses()
    {
        int[] processIds = FindUpdateGameProcesses();
        return Serialize(new { gameRunning = processIds.Length > 0, processIds });
    }

    private async Task<JToken> CloseGameForUpdateAsync()
    {
        GameInstallValidation game = RequireGame();
        IReadOnlyList<IUpdateGameProcess> processes = GameUpdateShutdownCoordinator.FindRunning(game.ExecutablePath);
        try
        {
            GameUpdateShutdownResult result = await _gameUpdateShutdown.RequestCloseAndWaitAsync(
                processes,
                TimeSpan.FromSeconds(30),
                _lifetime);
            AddLog(result.Success ? "success" : "error", result.Message);
            return Serialize(result);
        }
        finally
        {
            foreach (IUpdateGameProcess process in processes) process.Dispose();
        }
    }

    private JToken StartUpdate(int desktopProcessId)
    {
        int? gameProcessId = FindUpdateGameProcesses().Cast<int?>().FirstOrDefault();
        (bool success, string message) = _updates.StartApply(_settings, gameProcessId, desktopProcessId);
        if (!success) throw new InvalidOperationException(message);
        AddLog("info", message);
        _ = _emit("updateStarted", new { message });
        return Serialize(new { success, message });
    }

    private JToken OpenEvidenceDirectory()
    {
        string? path = _status?.EvidenceDirectory;
        if (string.IsNullOrWhiteSpace(path)) path = _status?.ArtifactDirectory;
        if (string.IsNullOrWhiteSpace(path)) path = _hello?.ArtifactRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new InvalidOperationException("当前没有可打开的证据目录。");
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(path) }, UseShellExecute = false });
        return Serialize(new { path });
    }

    private JToken OpenSaveBackupDirectory()
    {
        string path = _saveBackups.EnsureBackupRoot(_settings.GameRoot);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            ArgumentList = { path },
            UseShellExecute = false
        });
        return Serialize(new { path });
    }

    private JToken ListSaveBackups()
    {
        IReadOnlyList<SaveBackupEntry> backups = _saveBackups.ListBackups(
            _game?.GameRoot ?? _settings.GameRoot,
            _lifetime);
        return Serialize(new
        {
            backups,
            status = _saveBackups.Snapshot(
                _settings.AutomaticSaveBackupEnabled,
                _settings.MaximumSaveBackups)
        });
    }

    private async Task<JToken> RestoreSaveBackupAsync(string backupId)
    {
        GameInstallValidation game = RequireGame();
        string activeSaveRoot = _status?.ActivationMode == AutoPlayerActivationMode.ResidentPlayer
            ? _status.ActiveSaveRoot
            : string.Empty;
        SaveRestorePlan plan = _saveBackups.CreateRestorePlan(game.GameRoot, backupId, activeSaveRoot);
        IReadOnlyList<IUpdateGameProcess> processes = GameUpdateShutdownCoordinator.FindRunning(game.ExecutablePath);
        try
        {
            GameUpdateShutdownResult shutdown = await _gameUpdateShutdown.RequestCloseAndWaitAsync(
                processes,
                TimeSpan.FromSeconds(30),
                _lifetime);
            if (!shutdown.Success) throw new InvalidOperationException(shutdown.Message.Replace("更新", "读档", StringComparison.Ordinal));
        }
        finally
        {
            foreach (IUpdateGameProcess process in processes) process.Dispose();
        }

        ResetSession();
        SaveRestoreResult restored = await _saveBackups.RestoreAsync(plan, _lifetime);
        bool gameRestarted = false;
        string message = restored.Message;
        try
        {
            LaunchGame();
            gameRestarted = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            message = $"已恢复 {restored.BackupId}，但游戏未能自动启动：{exception.Message}";
        }

        AddLog(gameRestarted ? "success" : "warn", message);
        await EmitSnapshotAsync();
        return Serialize(new
        {
            success = true,
            restored.BackupId,
            restored.TargetDirectory,
            gameRestarted,
            message,
            backups = _saveBackups.ListBackups(game.GameRoot, _lifetime)
        });
    }

    private async Task ObserveSaveBackupsAsync(AutoPlayerStatus? status)
    {
        try
        {
            string? message = await _saveBackups.ObserveAsync(
                status,
                _settings,
                _game?.GameRoot ?? _settings.GameRoot,
                _lifetime);
            if (!string.IsNullOrWhiteSpace(message)) AddLog("backup", message, emit: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddLog("warn", "自动备份暂时无法完成：" + exception.Message, emit: false);
        }
    }

    private JToken ClearLogs()
    {
        _logs.Clear();
        _ = EmitSnapshotAsync();
        return Serialize(new { success = true });
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(850));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!await _operationGate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                await PollOnceAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddLog("error", "后台连接检查失败：" + exception.Message);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private async Task PollOnceAsync()
    {
        if (_game == null || _pluginStatus?.State != PluginState.Enabled)
        {
            _connectionLabel = _game == null ? "等待选择游戏" : "插件未启用";
            return;
        }

        PrepareInstalledSession(selectProfile: false, replaceExisting: false);
        if (_session == null) return;
        IReadOnlyList<int> processes = FindRunningGameProcesses(_game.ExecutablePath);
        if (processes.Count != 1)
        {
            if (processes.Count > 1)
            {
                _connectionLabel = "检测到多个游戏进程";
                _connectionReason = "请只保留一个相同目录的 Skyspine 进程。";
            }
            else
            {
                _connectionLabel = "等待游戏连接";
                _connectionReason = string.Empty;
            }
            InvalidateTrust();
            _session.ProcessId = null;
            return;
        }

        if (_session.ProcessId != processes[0])
        {
            InvalidateTrust();
            BindProcess(processes[0]);
        }

        foreach (string line in _logTail.ReadAvailable(120)) AddLog("game", line, emit: false);

        PipeCallResult call = !_trusted || _hello == null
            ? await _pipeClient.HelloAsync(_session, _lifetime)
            : await _pipeClient.StatusAsync(_session, _lifetime);
        if (!call.TransportSuccess)
        {
            bool wasTrusted = _trusted;
            InvalidateTrust();
            _connectionLabel = "等待插件响应";
            _connectionReason = call.Error;
            if (wasTrusted || _pollConnectedLastTime) AddLog("warn", "插件连接已中断，正在自动重新握手。");
            _pollConnectedLastTime = false;
            await EmitSnapshotAsync();
            return;
        }

        ControlResponse response = call.Response!;
        if (!response.Success)
        {
            InvalidateTrust();
            _connectionLabel = "插件拒绝连接";
            _connectionReason = response.Message;
            await EmitSnapshotAsync();
            return;
        }

        if (response.Hello != null && !_trusted)
        {
            if (!ValidateHello(response.Hello, out string error))
            {
                _connectionLabel = "安全验证未通过";
                _connectionReason = error;
                await EmitSnapshotAsync();
                return;
            }

            _hello = response.Hello;
            _trusted = true;
            _session.ProcessId = _hello.GameProcessId;
            _session.ProcessInstanceId = _hello.ProcessInstanceId;
            _connectionLabel = "游戏已安全连接";
            _connectionReason = string.Empty;
            if (!_pollConnectedLastTime) AddLog("safe", "插件、游戏进程路径和程序集指纹已经交叉验证。");
            _pollConnectedLastTime = true;
        }
        if (response.Status != null) _status = response.Status;
        await ObserveSaveBackupsAsync(_status);
        await EnsureDefaultCheatEnabledAsync();
        await EmitSnapshotAsync();
    }

    private async Task EnsureDefaultCheatEnabledAsync()
    {
        if (!_trusted || _session == null || DateTime.UtcNow < _nextAutomaticCheatEnableUtc) return;
        bool available = _status?.CheatAvailable == true || _hello?.CheatAvailable == true;
        bool authorized = _status?.CheatSessionAuthorized == true || _hello?.CheatSessionAuthorized == true;
        bool enabled = _status?.CheatModeEnabled == true || _hello?.CheatModeEnabled == true;
        if (!available || !authorized || enabled) return;

        _nextAutomaticCheatEnableUtc = DateTime.UtcNow.AddSeconds(5);
        PipeCallResult call = await _pipeClient.SendCheatAsync(
            _session,
            CheatCommands.SetEnabled,
            new JObject { ["enabled"] = true },
            _lifetime);
        if (!call.TransportSuccess)
        {
            InvalidateTrust();
            _connectionLabel = "等待插件响应";
            _connectionReason = call.Error;
            return;
        }

        ControlResponse response = call.Response!;
        if (response.Status != null) _status = response.Status;
        if (response.Success)
        {
            if (_hello != null) _hello.CheatModeEnabled = true;
            _lastAutomaticCheatEnableError = string.Empty;
            AddLog("cheat", "作弊功能已按默认设置自动开启。", emit: false);
            return;
        }

        if (!string.Equals(_lastAutomaticCheatEnableError, response.Message, StringComparison.Ordinal))
        {
            _lastAutomaticCheatEnableError = response.Message;
            AddLog("warn", "作弊功能暂时无法自动开启：" + response.Message, emit: false);
        }
    }

    private void RefreshPluginStatus()
    {
        _pluginStatus = _game == null ? null : _installer.GetStatus(_game.GameRoot);
    }

    private void PrepareInstalledSession(bool selectProfile, bool replaceExisting = true)
    {
        if (_game == null) return;
        if (!replaceExisting && _session != null) return;
        ActivationSession next = _installedSessions.Ensure(_game, _settings.ProfileName, selectProfile);
        if (_session != null
            && string.Equals(_session.Ticket.PipeName, next.Ticket.PipeName, StringComparison.Ordinal)
            && string.Equals(_session.Ticket.Token, next.Ticket.Token, StringComparison.Ordinal)) return;
        AdoptSession(next);
    }

    private void AdoptSession(ActivationSession session, bool includeExistingLog = false)
    {
        _session = session;
        _hello = null;
        _status = null;
        _trusted = false;
        _pollConnectedLastTime = false;
        _nextAutomaticCheatEnableUtc = default;
        _lastAutomaticCheatEnableError = string.Empty;
        _logTail.Reset(session.LogPath, startAtEnd: !includeExistingLog);
    }

    private void BindProcess(int processId)
    {
        if (_session == null || _game == null) return;
        _session.ProcessId = processId;
        _session.ProcessStartTimeUtc = TryGetGameProcessStartTimeUtc(processId, _game.ExecutablePath, out DateTime started)
            ? started
            : null;
        _session.ProcessInstanceId = string.Empty;
    }

    private void ResetSession()
    {
        _session = null;
        _hello = null;
        _status = null;
        _trusted = false;
        _pollConnectedLastTime = false;
        _nextAutomaticCheatEnableUtc = default;
        _lastAutomaticCheatEnableError = string.Empty;
        _connectionLabel = "等待游戏连接";
        _connectionReason = string.Empty;
    }

    private void InvalidateTrust()
    {
        _trusted = false;
        _hello = null;
        if (_session != null) _session.ProcessInstanceId = string.Empty;
    }

    private bool ValidateHello(BridgeHello hello, out string error)
    {
        if (_game == null || _session == null)
        {
            error = "Host 当前没有经过验证的游戏会话。";
            return false;
        }
        if (hello.ProtocolVersion != Protocol.CurrentVersion)
        {
            error = $"插件协议不兼容：Host v{Protocol.CurrentVersion}，插件 v{hello.ProtocolVersion}。";
            return false;
        }
        if (!Guid.TryParseExact(hello.ProcessInstanceId, "N", out _))
        {
            error = "插件未返回有效的进程实例标识。";
            return false;
        }
        if (!ValidateGameProcess(hello.GameProcessId, _game.ExecutablePath, out error)) return false;
        if (!string.Equals(hello.AssemblySha256, _game.AssemblySha256, StringComparison.OrdinalIgnoreCase)
            || !hello.ProductIdentityValid || !hello.FingerprintAccepted)
        {
            error = "插件报告的产品身份或程序集指纹与所选游戏不一致。";
            return false;
        }
        if (!hello.RuntimeContractAvailable)
        {
            error = "当前游戏缺少插件需要的运行时成员。";
            return false;
        }
        if (hello.ActivationMode != AutoPlayerActivationMode.ResidentPlayer
            || !AutoPlayerSafetyGate.IsReady(
                AutoPlayerActivationMode.ResidentPlayer,
                hello.SaveIsolationApplied,
                hello.SaveIsolationVerified,
                hello.PlatformWritesBlocked,
                hello.GameArtifactsRedirected))
        {
            error = "玩家模式安全门禁未通过。";
            return false;
        }
        if (!SamePath(hello.ProfileRoot, _session.Ticket.ProfileRoot)
            || !SamePath(hello.ArtifactRoot, _session.Ticket.ArtifactRoot))
        {
            error = "插件使用的状态目录不属于当前本机控制注册。";
            return false;
        }
        if (hello.CheatSessionAuthorized != _session.Ticket.CheatModeAllowed
            || hello.CheatSessionAuthorized && hello.CheatProtocolVersion != Protocol.CheatCurrentVersion)
        {
            error = "作弊控制授权或协议与本机注册不一致。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private object BuildSnapshot() => new
    {
        protocolVersion = DesktopHostProtocol.CurrentVersion,
        version = ManagerProductInfo.Version,
        settings = _settings,
        game = _game,
        plugin = _pluginStatus,
        connection = new
        {
            trusted = _trusted,
            label = _connectionLabel,
            reason = _connectionReason,
            processId = _session?.ProcessId,
            cheatAvailable = _hello?.CheatAvailable == true || _status?.CheatAvailable == true,
            autoplayActive = _status?.RunState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused
        },
        hello = _hello,
        status = _status,
        saveBackups = _saveBackups.Snapshot(
            _settings.AutomaticSaveBackupEnabled,
            _settings.MaximumSaveBackups),
        update = _updateStatus,
        logs = _logs
    };

    private async Task EmitSnapshotAsync() => await _emit("snapshot", BuildSnapshot());

    private void AddLog(string level, string message, bool emit = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _logs.Add(new HostLogEntry(DateTime.UtcNow, level, message.Trim()));
        if (_logs.Count > MaximumLogEntries) _logs.RemoveRange(0, _logs.Count - MaximumLogEntries);
        if (emit) _ = _emit("log", _logs[^1]);
    }

    private int[] FindUpdateGameProcesses() => _game == null
        ? Array.Empty<int>()
        : FindRunningGameProcesses(_game.ExecutablePath).ToArray();

    private static IReadOnlyList<int> FindRunningGameProcesses(string executablePath)
    {
        List<int> result = new();
        if (string.IsNullOrWhiteSpace(executablePath)) return result;
        string expected = Path.GetFullPath(executablePath);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && SamePath(process.MainModule?.FileName ?? string.Empty, expected))
                        result.Add(process.Id);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
                {
                }
            }
        }
        result.Sort();
        return result;
    }

    internal static bool ValidateGameProcess(int processId, string expectedExecutable, out string error)
    {
        if (processId <= 0)
        {
            error = "插件游戏进程 PID 无效。";
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited || !SamePath(process.MainModule?.FileName ?? string.Empty, expectedExecutable))
            {
                error = "插件进程不属于当前选择的游戏目录。";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            error = "无法验证插件游戏进程：" + exception.Message;
            return false;
        }
    }

    internal static bool TryGetGameProcessStartTimeUtc(int processId, string expectedExecutable, out DateTime started)
    {
        started = default;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited || !SamePath(process.MainModule?.FileName ?? string.Empty, expectedExecutable)) return false;
            started = process.StartTime.ToUniversalTime();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void InlineVerifiedCatalogIcons(JObject data)
    {
        string root = _hello?.ArtifactRoot ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        foreach (JObject item in data.DescendantsAndSelf().OfType<JObject>())
        {
            string relative = item.Value<string>("iconFile") ?? string.Empty;
            string expectedHash = item.Value<string>("iconSha256") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || expectedHash.Length != 64) continue;
            try
            {
                string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
                string relativeCheck = Path.GetRelativePath(normalizedRoot, candidate);
                if (Path.IsPathRooted(relativeCheck) || relativeCheck.StartsWith("..", StringComparison.Ordinal)) continue;
                FileInfo file = new(candidate);
                if (!file.Exists || file.Length is <= 0 or > 4 * 1024 * 1024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                byte[] bytes = File.ReadAllBytes(candidate);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), SHA256.HashData(bytes))) continue;
                item["iconDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
            }
        }
    }

    private GameInstallValidation RequireGame() => _game ?? throw new InvalidOperationException("请先选择并验证游戏目录。");

    private static string RequiredString(JObject? parameters, string name)
    {
        string value = parameters?.Value<string>(name)?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"缺少参数：{name}")
            : value;
    }

    private static JToken Serialize(object value) => JToken.FromObject(value, CamelSerializer);
    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "未知版本" : value;
    private static string ShortHash(string value) => value.Length <= 18 ? value : value[..10] + "..." + value[^6..];
    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollLifetime.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask; }
            catch (OperationCanceledException) { }
        }
        _pollLifetime.Dispose();
        _operationGate.Dispose();
    }
}
