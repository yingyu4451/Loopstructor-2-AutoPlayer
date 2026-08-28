using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json.Linq;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class MainForm : Window
{
    private static readonly Brush NormalStageBackground = CreateBrush(58, 38, 24);
    private static readonly Brush WarningStageBackground = CreateBrush(60, 42, 19);
    private static readonly Brush RestartStageBackground = CreateBrush(61, 29, 22);

    private readonly ManagerLaunchOptions _launchOptions;
    private readonly ManagerSettingsStore _settingsStore;
    private readonly DistributionLayout _distribution;
    private readonly GameInstallValidator _validator = new();
    private readonly BepInExConfigWriter _configWriter = new();
    private readonly ActivationSessionFactory _sessionFactory = new();
    private readonly InstalledControlSessionStore _installedSessions = new();
    private readonly PipeControlClient _pipeClient = new();
    private readonly LogTailReader _logTail = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _pollTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(850)
    };
    private readonly ObservableCollection<TelemetryRow> _telemetryRows = new();
    private readonly Dictionary<string, TelemetryRow> _telemetry = new(StringComparer.Ordinal);
    private readonly ObservableCollection<TimelineDisplayItem> _timeline = new();
    private readonly ObservableCollection<AutomationModeOption> _modeOptions = new();
    private readonly ObservableCollection<AutomationCharacterOption> _characterOptions = new();

    private ManagerSettings _settings;
    private BepInExInstaller _installer;
    private GameLauncher _gameLauncher;
    private UpdateCoordinator _updates;
    private GameInstallValidation? _game;
    private ActivationSession? _session;
    private PluginInstallStatus? _pluginStatus;
    private BridgeHello? _hello;
    private AutoPlayerStatus? _status;
    private CheatForm? _cheatForm;
    private bool _pollInProgress;
    private bool _sessionTrusted;
    private bool _legacyProbeDone;
    private bool _updateAvailable;
    private string _latestUpdateVersion = string.Empty;
    private bool _contentShown;
    private int _transportFailures;
    private string _lastStatusSignature = string.Empty;
    private string _timelineSignature = string.Empty;
    private string _lastTrustError = string.Empty;
    private bool _restartWarningReported;
    private bool _cheatMarkerReported;
    private bool _bindingUiScale;
    private bool _automationSetupLoaded;
    private DateTime _nextAutomationSetupQueryUtc;

    public MainForm(ManagerLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        _settingsStore = new ManagerSettingsStore();
        _settings = _settingsStore.Load(out string settingsWarning);
        _distribution = DistributionLayout.Locate();
        _installer = new BepInExInstaller(_distribution, _configWriter);
        _gameLauncher = new GameLauncher(_sessionFactory, _configWriter);
        _updates = new UpdateCoordinator(_distribution);

        InitializeComponent();
        InitializeWindow();
        InitializeSelectors();
        InitializeTelemetry();
        BindSettings();
        UiScaleService.Register(this, _settings);
        SetOperationAvailability();

        _pollTimer.Tick += PollTimerOnTick;
        ContentRendered += async (_, _) =>
        {
            if (_contentShown) return;
            _contentShown = true;
            await OnShownAsync(settingsWarning);
        };
        Closing += OnWindowClosing;
    }

    private Brush SignalBrush => ThemeBrush("SignalGreenBrush");
    private Brush GoldBrush => ThemeBrush("GoldBrush");
    private Brush DangerBrush => ThemeBrush("DangerBrush");
    private Brush BlueBrush => ThemeBrush("OperationBlueBrush");
    private Brush TextBrush => ThemeBrush("TextBrush");
    private Brush MutedBrush => ThemeBrush("MutedTextBrush");

    private void InitializeWindow()
    {
        Title = $"Loopstructor 2.AutoPlayer Manager — v{ManagerProductInfo.Version}";
        ProductVersionLabel.Text = ManagerProductInfo.DisplayText;
        if (_launchOptions.WindowSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }
    }

    private void InitializeSelectors()
    {
        _mode.ItemsSource = _modeOptions;
        _character.ItemsSource = _characterOptions;
        _modeOptions.Add(new AutomationModeOption
        {
            Mode = AutomationGameMode.Common,
            DisplayName = "普通模式",
            Available = false,
            Reason = "连接游戏后读取可玩内容"
        });
        _modeOptions.Add(new AutomationModeOption
        {
            Mode = AutomationGameMode.Random,
            DisplayName = "随机模式",
            Available = false,
            Reason = "连接游戏后读取可玩内容"
        });
        _speed.ItemsSource = new[] { "跟随游戏", "1x · 常速（推荐）", "2x · 加速", "3x · 高速" };
        _decisionPriority.ItemsSource = new[] { "优先拿三星车", "优先拿弹射点", "优先拿遗物" };
        _uiScaleMode.ItemsSource = new[] { "跟随系统 DPI", "自定义" };
        _uiScalePercent.ItemsSource = Enumerable.Range(0, 26)
            .Select(index => 75 + index * 5)
            .Select(value => value + "%")
            .ToArray();
        _timelineItems.ItemsSource = _timeline;
        _logs.Document.PagePadding = new Thickness(0);
    }

    private void InitializeTelemetry()
    {
        (string Key, string Caption)[] definitions =
        {
            ("product", "产品"),
            ("gameVersion", "游戏版本"),
            ("pluginVersion", "插件版本"),
            ("protocol", "协议"),
            ("unity", "Unity"),
            ("buildGuid", "构建 GUID"),
            ("assembly", "程序集 SHA-256"),
            ("mvid", "程序集 MVID"),
            ("fingerprint", "指纹门禁"),
            ("runtime", "运行时合同"),
            ("isolation", "存档隔离"),
            ("platform", "平台写入"),
            ("artifacts", "产物重定向"),
            ("profile", "存档模式"),
            ("evidence", "证据目录"),
            ("integrity", "运行标记"),
            ("outcome", "本局结果"),
            ("waves", "波次"),
            ("chapter", "章节 / 地图层"),
            ("frameTiming", "游戏帧率"),
            ("runtimeTiming", "MCP 调用耗时"),
            ("process", "进程状态")
        };

        foreach ((string key, string caption) in definitions)
        {
            TelemetryRow row = new(key, caption);
            _telemetry[key] = row;
            _telemetryRows.Add(row);
        }

        _telemetryItems.ItemsSource = _telemetryRows;
    }

    private void BindSettings()
    {
        _settings.NormalizeUpdateSource();
        _gamePath.Text = _settings.GameRoot;
        _profileName.Text = string.IsNullOrWhiteSpace(_settings.ProfileName) ? "player-default" : _settings.ProfileName;
        _continueProfile.IsChecked = _settings.ContinueExistingProfile;
        _mode.SelectedItem = _modeOptions.First(option => option.Mode == _settings.GameMode);
        _speed.SelectedIndex = SpeedSelectionIndex(_settings.OverrideGameSpeed, _settings.SpeedState);
        _maxMinutes.Text = Math.Clamp(_settings.MaxRunMinutes, 5, 480).ToString();
        _skipStory.IsChecked = _settings.SkipStory;
        _decisionPriority.SelectedIndex = Math.Clamp((int)_settings.DecisionPriority, 0, 2);
        _bindingUiScale = true;
        _uiScaleMode.SelectedIndex = _settings.UiScaleMode == UiScaleMode.Custom ? 1 : 0;
        _uiScalePercent.SelectedIndex = Math.Clamp((_settings.CustomUiScalePercent - 75) / 5, 0, 25);
        _uiScalePercent.IsEnabled = _settings.UiScaleMode == UiScaleMode.Custom;
        _bindingUiScale = false;
        UpdateCharacterVisibility();
    }

    private async Task OnShownAsync(string settingsWarning)
    {
        if (!string.IsNullOrWhiteSpace(settingsWarning))
        {
            AppendLog("WARN", settingsWarning, GoldBrush);
        }

        if (_launchOptions.DemoMode)
        {
            ApplyDemoMode();
            if (_launchOptions.DemoCheatWindow)
            {
                OpenCheatForm();
                _cheatForm?.SelectDemoTab(_launchOptions.DemoCheatTab);
            }

            if (_launchOptions.ScreenshotMode)
            {
                await CaptureScreenshotAsync();
            }

            return;
        }

        AppendLog("INFO", "Manager 已启动；验证已安装的 Skyspine 游戏后会自动等待并连接游戏进程。", TextBrush);
        if (!string.IsNullOrWhiteSpace(_settings.GameRoot))
        {
            await ValidateGameAsync(_settings.GameRoot);
        }

        if (ShouldCheckForUpdates(_launchOptions, _updates.IsConfigured(_settings)))
        {
            await CheckForUpdatesAsync(userInitiated: false);
        }
    }

    internal static bool ShouldCheckForUpdates(ManagerLaunchOptions options, bool updateSourceConfigured) =>
        !options.DemoMode && updateSourceConfigured;

    private async void BrowseButtonOnClick(object sender, RoutedEventArgs eventArgs) => await BrowseForGameAsync();
    private async void InstallButtonOnClick(object sender, RoutedEventArgs eventArgs) => await InstallPluginAsync();
    private void TogglePluginButtonOnClick(object sender, RoutedEventArgs eventArgs) => TogglePlugin();
    private void UninstallButtonOnClick(object sender, RoutedEventArgs eventArgs) => UninstallPlugin();
    private void LaunchButtonOnClick(object sender, RoutedEventArgs eventArgs) => LaunchGame();
    private void CheatButtonOnClick(object sender, RoutedEventArgs eventArgs) => OpenCheatForm();
    private async void StartButtonOnClick(object sender, RoutedEventArgs eventArgs) => await SendControlAsync("start");
    private async void PauseButtonOnClick(object sender, RoutedEventArgs eventArgs) => await SendControlAsync("pause");
    private async void ResumeButtonOnClick(object sender, RoutedEventArgs eventArgs) => await SendControlAsync("resume");
    private async void StopButtonOnClick(object sender, RoutedEventArgs eventArgs) => await SendControlAsync("stop");
    private async void UpdateButtonOnClick(object sender, RoutedEventArgs eventArgs) => await UpdateButtonOnClickAsync();
    private void OpenEvidenceButtonOnClick(object sender, RoutedEventArgs eventArgs) => OpenEvidenceDirectory();
    private void ClearLogsButtonOnClick(object sender, RoutedEventArgs eventArgs) => _logs.Document.Blocks.Clear();

    private void MaxMinutesOnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        _maxMinutes.Text = ParseClamped(_maxMinutes.Text, 5, 480, 120).ToString();
    }

    private void UiScaleSettingOnChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_bindingUiScale || !IsInitialized) return;
        _settings.UiScaleMode = _uiScaleMode.SelectedIndex == 1 ? UiScaleMode.Custom : UiScaleMode.System;
        _settings.CustomUiScalePercent = 75 + Math.Max(0, _uiScalePercent.SelectedIndex) * 5;
        _uiScalePercent.IsEnabled = _settings.UiScaleMode == UiScaleMode.Custom;
        UiScaleService.ApplyAll(_settings);
    }

    private void ModeSelectionOnChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        UpdateCharacterVisibility();
        SetOperationAvailability();
    }

    private void ContinueProfileOnChanged(object sender, RoutedEventArgs eventArgs)
    {
        UpdateCharacterVisibility();
        SetOperationAvailability();
    }

    private void UpdateCharacterVisibility()
    {
        bool visible = _mode.SelectedItem is AutomationModeOption { Mode: AutomationGameMode.Common } &&
                       _continueProfile.IsChecked != true;
        _characterField.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_skipStoryField, visible ? 1 : 0);
        Grid.SetColumn(_decisionPriorityField, visible ? 2 : 1);
        _secondaryOptionThirdColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private async Task BrowseForGameAsync()
    {
        if (_launchOptions.DemoMode) return;
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "选择 Loopstructor 2: Skyspine 的 Windows 打包目录",
            InitialDirectory = Directory.Exists(_gamePath.Text) ? _gamePath.Text : string.Empty,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ValidateGameAsync(dialog.FolderName);
        }
    }

    private async Task ValidateGameAsync(string root)
    {
        SetBusy(true);
        _gamePath.Text = root;
        _validationState.Text = "正在验证构建...";
        _validationState.Foreground = BlueBrush;
        try
        {
            GameInstallValidation validation = await _validator.ValidateAsync(root, _lifetime.Token);
            _game = validation.IsValid ? validation : null;
            if (validation.IsValid)
            {
                _settings.GameRoot = validation.GameRoot;
                _gamePath.Text = validation.GameRoot;
                _validationState.Text = $"已验证 Skyspine {Display(validation.ProductVersion)} / {ShortHash(validation.AssemblySha256)}";
                _validationState.Foreground = SignalBrush;
                AppendLog("SAFE", "已验证所选 Skyspine 游戏及自动化运行时合同。", SignalBrush);
                foreach (string warning in validation.Warnings)
                {
                    AppendLog("WARN", warning, GoldBrush);
                }

                ApplyBuildTelemetry(validation);
                RefreshPluginStatus();
                if (_pluginStatus?.State == PluginState.Enabled)
                {
                    PrepareInstalledSession(validation, selectProfile: false, announce: true);
                }
                SaveSettings();
            }
            else
            {
                _validationState.Text = validation.Errors.FirstOrDefault() ?? "构建验证失败";
                _validationState.Foreground = DangerBrush;
                foreach (string error in validation.Errors)
                {
                    AppendLog("ERROR", error, DangerBrush);
                }

                ClearBuildTelemetry();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            SetBusy(false);
            SetOperationAvailability();
        }
    }

    private async Task InstallPluginAsync()
    {
        if (_launchOptions.DemoMode || _game == null) return;
        SetBusy(true);
        try
        {
            PluginOperationResult result = await _installer.InstallAsync(_game, _lifetime.Token);
            AppendOperation(result);
            RefreshPluginStatus();
            if (result.Success && _pluginStatus?.State == PluginState.Enabled)
            {
                PrepareInstalledSession(_game, selectProfile: true, announce: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            SetBusy(false);
            SetOperationAvailability();
        }
    }

    private void TogglePlugin()
    {
        if (_launchOptions.DemoMode || _game == null || _pluginStatus == null) return;
        bool enable = _pluginStatus.State == PluginState.Disabled;
        PluginOperationResult result = _installer.SetEnabled(_game.GameRoot, enable);
        AppendOperation(result);
        RefreshPluginStatus();
        if (result.Success && enable && _pluginStatus?.State == PluginState.Enabled)
        {
            PrepareInstalledSession(_game, selectProfile: false, announce: true);
        }
    }

    private void UninstallPlugin()
    {
        if (_launchOptions.DemoMode || _game == null) return;
        MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            this,
            "仅删除 AutoPlayer 插件及其配置，保留共享 BepInEx 运行时。继续？",
            "卸载 AutoPlayer 插件",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        PluginOperationResult result = _installer.Uninstall(_game.GameRoot);
        AppendOperation(result);
        if (result.Success)
        {
            _installedSessions.Delete(_game.GameRoot);
            ResetSession();
        }
        RefreshPluginStatus();
    }

    private void LaunchGame()
    {
        if (_launchOptions.DemoMode) return;
        if (_game == null || _pluginStatus?.State != PluginState.Enabled)
        {
            AppendLog("ERROR", "启动前必须验证游戏并启用插件。", DangerBrush);
            return;
        }

        if (_sessionTrusted && _session?.ProcessId is > 0)
        {
            AppendLog("INFO", "已经连接到当前 Skyspine 游戏，无需重复启动。", BlueBrush);
            return;
        }

        IReadOnlyList<int> runningProcessIds = FindRunningGameProcesses(_game.ExecutablePath);
        int? preferredProcessId = _session != null
                                  && MatchesBoundGameProcess(
                                      _session.ProcessId,
                                      _session.ProcessStartTimeUtc,
                                      _game.ExecutablePath)
            ? _session.ProcessId
            : null;
        int? runningProcessId = SelectRunningGameProcess(preferredProcessId, runningProcessIds);
        if (runningProcessId is > 0)
        {
            PrepareInstalledSession(_game, selectProfile: false, announce: false);
            if (_session != null)
            {
                _session.ProcessId = runningProcessId.Value;
                _session.ProcessStartTimeUtc = TryGetGameProcessStartTimeUtc(
                    runningProcessId.Value,
                    _game.ExecutablePath,
                    out DateTime startTimeUtc)
                    ? startTimeUtc
                    : null;
            }
            SetConnectionState("正在连接", GoldBrush);
            AppendLog(
                "INFO",
                $"检测到当前游戏已经运行（PID {runningProcessId}），不会重复启动；正在连接玩家模式插件。",
                BlueBrush);
            _pollTimer.Start();
            _ = PollPluginAsync();
            return;
        }

        if (runningProcessIds.Count > 1)
        {
            SetConnectionState("多个游戏进程", DangerBrush);
            AppendLog(
                "ERROR",
                "检测到多个相同目录的 Skyspine 游戏进程，无法安全判断控制目标。请只保留一个游戏进程后重试。",
                DangerBrush);
            return;
        }

        SaveSettings();
        ActivationSession installedSession;
        try
        {
            installedSession = _installedSessions.Ensure(_game, _settings.ProfileName, selectProfile: true);
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "无法准备玩家模式本机控制注册：" + exception.Message, DangerBrush);
            return;
        }

        GameLaunchResult result = _gameLauncher.Launch(_game, installedSession);
        if (!result.Success || result.Session == null)
        {
            AppendLog("ERROR", result.Message, DangerBrush);
            return;
        }

        AdoptSession(result.Session, includeExistingLog: true);
        SetConnectionState("等待插件", GoldBrush);
        SetStageState("启动中 / 安全握手", "正在核对进程路径、程序集指纹与本机控制凭据", SignalBrush, NormalStageBackground);
        AppendLog("INFO", result.Message, BlueBrush);
        AppendLog("SAFE", "只接受所选游戏目录、当前 SHA-256 与本机令牌对应的插件。", SignalBrush);
        AppendLog("INFO", "玩家模式不会重定向游戏存档或平台写入；连接后可随时开始、暂停或停止自动游玩。", TextBrush);
        AppendLog("CHEAT", "安全握手通过后可随时打开作弊工具；自动游玩期间仍可查看怪物 ID 与 Buff，其他作弊写操作会被锁定。", GoldBrush);
        _pollTimer.Start();
        SetOperationAvailability();
        _ = PollPluginAsync();
    }

    private void PrepareInstalledSession(
        GameInstallValidation game,
        bool selectProfile,
        bool announce)
    {
        try
        {
            ActivationSession next = _installedSessions.Ensure(
                game,
                _profileName.Text,
                selectProfile);
            if (SameControlSession(_session, next))
            {
                _pollTimer.Start();
                return;
            }

            AdoptSession(next);
            SetConnectionState("等待游戏", GoldBrush);
            SetStageState(
                "玩家模式 / 后台待命",
                "已安装本机控制凭据；游戏运行时 Manager 会自动连接",
                GoldBrush,
                NormalStageBackground);
            if (announce)
            {
                AppendLog(
                    "INFO",
                    "玩家模式已就绪：可以先启动游戏，也可以让 Manager 启动；连接后可随时控制自动游玩。",
                    TextBrush);
            }

            _pollTimer.Start();
            _ = PollPluginAsync();
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "无法创建玩家模式本机控制注册：" + exception.Message, DangerBrush);
        }
    }

    private void AdoptSession(ActivationSession session, bool includeExistingLog = false)
    {
        _session = session;
        _hello = null;
        _status = null;
        _sessionTrusted = false;
        _legacyProbeDone = false;
        _transportFailures = 0;
        _lastStatusSignature = string.Empty;
        _lastTrustError = string.Empty;
        _restartWarningReported = false;
        _cheatMarkerReported = false;
        _cheatForm?.UpdateSession(false, null, null);
        // Persistent player sessions reuse one artifact directory. When Manager
        // attaches, old Player.log content belongs to an earlier game process.
        _logTail.Reset(session.LogPath, startAtEnd: !includeExistingLog);
    }

    private void ResetSession()
    {
        _pollTimer.Stop();
        _session?.DeleteTicket();
        _session = null;
        _hello = null;
        _status = null;
        _sessionTrusted = false;
        _transportFailures = 0;
        _cheatForm?.UpdateSession(false, null, null);
        SetConnectionState("未连接", MutedBrush);
        SetOperationAvailability();
    }

    private static bool SameControlSession(ActivationSession? first, ActivationSession second) =>
        first != null
        && first.ActivationMode == second.ActivationMode
        && string.Equals(first.Ticket.PipeName, second.Ticket.PipeName, StringComparison.Ordinal)
        && string.Equals(first.Ticket.Token, second.Ticket.Token, StringComparison.Ordinal)
        && string.Equals(first.Ticket.ExpectedAssemblySha256, second.Ticket.ExpectedAssemblySha256, StringComparison.OrdinalIgnoreCase)
        && SamePath(first.Ticket.ProfileRoot, second.Ticket.ProfileRoot)
        && SamePath(first.Ticket.ArtifactRoot, second.Ticket.ArtifactRoot);

    private void ReloadPersistentSessionAfterRejection()
    {
        if (_session?.IsPersistent != true || _game == null) return;
        int? processId = _session.ProcessId;
        DateTime? processStartTimeUtc = _session.ProcessStartTimeUtc;
        if (!_installedSessions.TryLoad(_game, out ActivationSession? refreshed, out _)
            || refreshed == null
            || SameControlSession(_session, refreshed))
        {
            return;
        }

        refreshed.ProcessId = processId;
        refreshed.ProcessStartTimeUtc = processStartTimeUtc;
        AdoptSession(refreshed);
        AppendLog("INFO", "已重新加载玩家模式本机控制凭据；正在重新握手。", BlueBrush);
    }

    private static IReadOnlyList<int> FindRunningGameProcesses(string executablePath)
    {
        List<int> matches = new();
        string expected = Path.GetFullPath(executablePath);
        string processName = Path.GetFileNameWithoutExtension(expected);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(processPath)
                        && SamePath(processPath, expected)
                        && !process.HasExited)
                    {
                        matches.Add(process.Id);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Processes outside the current desktop/user boundary are not attachable.
                }
            }
        }

        matches.Sort();
        return matches;
    }

    internal static int? SelectRunningGameProcess(
        int? preferredProcessId,
        IReadOnlyCollection<int> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (preferredProcessId is > 0 && candidates.Contains(preferredProcessId.Value))
        {
            return preferredProcessId.Value;
        }

        return candidates.Count == 1 ? candidates.First() : null;
    }

    private bool EnsureResidentProcessTarget()
    {
        if (_session == null) return false;
        if (_session.ActivationMode != AutoPlayerActivationMode.ResidentPlayer) return true;
        if (_game == null) return false;

        int? previousProcessId = _session.ProcessId;
        DateTime? previousStartTimeUtc = _session.ProcessStartTimeUtc;
        if (MatchesBoundGameProcess(
                previousProcessId,
                previousStartTimeUtc,
                _game.ExecutablePath))
        {
            return true;
        }

        InvalidateTransportTrust();
        _hello = null;
        _status = null;

        IReadOnlyList<int> candidates = FindRunningGameProcesses(_game.ExecutablePath);
        // A stale PID is not a preference. It may already belong to a new game
        // instance and must pass the same ambiguity rules as every other candidate.
        int? nextProcessId = SelectRunningGameProcess(preferredProcessId: null, candidates);
        if (candidates.Count > 1 && nextProcessId is not > 0)
        {
            InvalidateTransportTrust();
            _hello = null;
            _status = null;
            _session.ProcessId = null;
            _session.ProcessStartTimeUtc = null;
            _lastStatusSignature = string.Empty;
            const string ambiguity = "检测到多个相同目录的 Skyspine 游戏进程；请只保留一个进程后再连接。";
            if (!string.Equals(_lastTrustError, ambiguity, StringComparison.Ordinal))
            {
                _lastTrustError = ambiguity;
                AppendLog("ERROR", ambiguity, DangerBrush);
            }
            SetConnectionState("多个游戏进程", DangerBrush);
            SetOperationAvailability();
            return false;
        }

        DateTime? nextStartTimeUtc = null;
        if (nextProcessId is > 0)
        {
            if (!TryGetGameProcessStartTimeUtc(
                    nextProcessId.Value,
                    _game.ExecutablePath,
                    out DateTime discoveredStartTimeUtc))
            {
                SetConnectionState("进程验证失败", DangerBrush);
                return false;
            }

            nextStartTimeUtc = discoveredStartTimeUtc;
        }

        if (previousProcessId == nextProcessId
            && previousStartTimeUtc == nextStartTimeUtc)
        {
            SetConnectionState("等待游戏", GoldBrush);
            return nextProcessId is > 0;
        }

        InvalidateTransportTrust();
        _hello = null;
        _status = null;
        _session.ProcessId = nextProcessId;
        _session.ProcessStartTimeUtc = nextStartTimeUtc;
        _session.ProcessInstanceId = string.Empty;
        _lastStatusSignature = string.Empty;
        _lastTrustError = string.Empty;
        _cheatForm?.UpdateSession(false, null, null);
        _logTail.Reset(_session.LogPath, startAtEnd: true);
        SetOperationAvailability();

        if (nextProcessId is > 0)
        {
            AppendLog(
                "INFO",
                $"已发现 Skyspine 游戏进程 PID {nextProcessId}；正在连接进程专属控制通道。",
                BlueBrush);
        }
        else if (previousProcessId is > 0)
        {
            AppendLog(
                "INFO",
                $"Skyspine 游戏进程 PID {previousProcessId} 已退出或 PID 已被其他程序复用；Manager 将继续后台等待。",
                GoldBrush);
        }

        return nextProcessId is > 0;
    }

    private async Task PollPluginAsync()
    {
        if (_pollInProgress || _session == null || _launchOptions.DemoMode) return;
        _pollInProgress = true;
        try
        {
            if (!EnsureResidentProcessTarget())
            {
                return;
            }

            ReadPlayerLog();

            PipeCallResult call = _hello == null || !_sessionTrusted
                ? await _pipeClient.HelloAsync(_session, _lifetime.Token)
                : await _pipeClient.StatusAsync(_session, _lifetime.Token);
            if (!call.TransportSuccess)
            {
                bool connectionWasTrusted = InvalidateTransportTrust();
                _transportFailures++;
                SetConnectionState("未连接", GoldBrush);
                if (connectionWasTrusted)
                {
                    AppendLog(
                        "WARN",
                        "插件控制通道已中断；Manager 已禁用控制并开始重新握手：" + call.Error,
                        GoldBrush);
                }
                else if (_transportFailures == 4)
                {
                    string prefix = _hello == null
                        ? "尚未收到已安装游戏的插件握手；Manager 会继续等待："
                        : "插件重新握手仍未成功；Manager 会继续等待：";
                    AppendLog("WARN", prefix + call.Error, GoldBrush);
                }

                if (_transportFailures >= 6 && !_legacyProbeDone)
                {
                    _legacyProbeDone = true;
                    if (_session.IsPersistent)
                    {
                        PipeCallResult unscoped = await _pipeClient.ProbeUnscopedHelloAsync(
                            _session,
                            _lifetime.Token);
                        if (unscoped.TransportSuccess)
                        {
                            AppendLog(
                                "ERROR",
                                "检测到仍在运行的旧版插件控制通道。请彻底关闭 Skyspine，再重新启动游戏以加载当前插件；Manager 不会向旧通道发送控制命令。",
                                DangerBrush);
                        }
                    }

                    PipeCallResult legacy = await _pipeClient.ProbeLegacyStatusAsync(_lifetime.Token);
                    if (legacy.TransportSuccess)
                    {
                        AppendLog(
                            "ERROR",
                            "检测到旧版固定管道，仅作诊断，Manager 不会向其发送控制命令。请重装当前插件并重启游戏。",
                            DangerBrush);
                    }
                }

                CheckSelectedProcessBoundary();
                return;
            }

            _transportFailures = 0;
            ControlResponse? response = call.Response;
            if (response == null) return;

            if (!response.Success)
            {
                InvalidateTransportTrust();
                _hello = null;
                _status = null;
                ReloadPersistentSessionAfterRejection();
                SetConnectionState("插件拒绝", DangerBrush);
                string rejection = "插件拒绝控制请求：" + response.Message;
                if (!string.Equals(rejection, _lastTrustError, StringComparison.Ordinal))
                {
                    _lastTrustError = rejection;
                    AppendLog("ERROR", rejection + " Manager 将重读本机凭据并重新握手。", DangerBrush);
                }
                return;
            }

            if (response.Hello != null && !_sessionTrusted)
            {
                _hello = response.Hello;
                _sessionTrusted = ValidateHello(_hello, out string trustError);
                if (!_sessionTrusted)
                {
                    SetConnectionState("门禁失败", DangerBrush);
                    if (!string.Equals(trustError, _lastTrustError, StringComparison.Ordinal))
                    {
                        _lastTrustError = trustError;
                        AppendLog("ERROR", trustError, DangerBrush);
                    }
                }
                else
                {
                    _lastTrustError = string.Empty;
                    int launchProcessId = _session.ProcessId ?? 0;
                    _session.ProcessId = _hello.GameProcessId;
                    _session.ProcessStartTimeUtc = TryGetGameProcessStartTimeUtc(
                        _hello.GameProcessId,
                        _game!.ExecutablePath,
                        out DateTime handshakeStartTimeUtc)
                        ? handshakeStartTimeUtc
                        : null;
                    _session.ProcessInstanceId = _hello.ProcessInstanceId;
                    _session.DeleteTicket();
                    SetConnectionState("安全连接", SignalBrush);
                    AppendLog("SAFE", "插件握手与本次构建指纹一致，控制通道已启用。", SignalBrush);
                    if (launchProcessId != _hello.GameProcessId)
                    {
                        AppendLog(
                            "SAFE",
                            $"已从启动进程 PID {launchProcessId} 切换为经路径验证的游戏 PID {_hello.GameProcessId}。",
                            SignalBrush);
                    }

                    AppendLog(
                        "SAFE",
                        _session.IsPersistent
                            ? "玩家模式本机凭据、游戏路径和进程身份已经交叉验证。"
                            : "一次性备用授权票据已在可信握手后清理。",
                        SignalBrush);
                }

                ApplyHello(_hello);
                _cheatForm?.UpdateSession(_sessionTrusted, _hello, _status);
            }

            if (response.Status != null)
            {
                ApplyStatus(response.Status);
            }

            if (_sessionTrusted &&
                ShouldRefreshAutomationSetup(_status) &&
                DateTime.UtcNow >= _nextAutomationSetupQueryUtc)
            {
                await RefreshAutomationSetupAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "轮询失败：" + exception.Message, DangerBrush);
        }
        finally
        {
            _pollInProgress = false;
            SetOperationAvailability();
        }
    }

    internal static bool ShouldRefreshAutomationSetup(AutoPlayerStatus? status) =>
        status?.RunState is not (AutoPlayerRunState.Running or AutoPlayerRunState.Paused);

    private async Task SendControlAsync(string command)
    {
        if (_launchOptions.DemoMode) return;
        if (string.Equals(command, "start", StringComparison.OrdinalIgnoreCase)
            && _status?.NeedsProcessRestart == true)
        {
            ShowRestartRequired();
            AppendLog(
                "WARN",
                "当前游戏进程已标记为必须重启，开始命令未发送。彻底关闭 Skyspine 后再由 Manager 重新启动。",
                GoldBrush);
            return;
        }

        if (!_sessionTrusted || _session == null)
        {
            AppendLog("ERROR", "安全握手未通过，控制命令未发送。", DangerBrush);
            return;
        }

        SetControlButtons(false);
        try
        {
            PipeCallResult result = command switch
            {
                "start" => await _pipeClient.StartAsync(_session, BuildRunOptions(), _lifetime.Token),
                "pause" => await _pipeClient.PauseAsync(_session, _lifetime.Token),
                "resume" => await _pipeClient.ResumeAsync(_session, _lifetime.Token),
                "stop" => await _pipeClient.StopAsync(_session, _lifetime.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            if (!result.TransportSuccess)
            {
                bool connectionWasTrusted = InvalidateTransportTrust();
                SetConnectionState("未连接", GoldBrush);
                if (connectionWasTrusted)
                {
                    AppendLog("WARN", "插件控制通道已中断；Manager 正在重新握手。", GoldBrush);
                }
                AppendLog("ERROR", $"{ControlCommandName(command)}发送失败：{result.Error}", DangerBrush);
                return;
            }

            ControlResponse response = result.Response!;
            if (IsSessionRejection(response))
            {
                InvalidateTransportTrust();
                _hello = null;
                _status = null;
                SetConnectionState("重新握手", GoldBrush);
            }
            AppendLog(response.Success ? "ACT" : "ERROR", response.Message, response.Success ? SignalBrush : DangerBrush);
            if (response.Status != null)
            {
                ApplyStatus(response.Status);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            SetOperationAvailability();
        }
    }

    private async Task<ControlResponse?> SendCheatCommandAsync(string command, JObject? arguments)
    {
        if (_launchOptions.DemoMode)
        {
            return DemoData.CheatResponse(command, arguments);
        }

        if (!_sessionTrusted || _session == null)
        {
            return new ControlResponse
            {
                Success = false,
                Message = "安全握手未通过，作弊命令未发送。"
            };
        }

        try
        {
            PipeCallResult result = await _pipeClient.SendCheatAsync(_session, command, arguments, _lifetime.Token);
            if (!result.TransportSuccess)
            {
                bool connectionWasTrusted = InvalidateTransportTrust();
                SetConnectionState("未连接", GoldBrush);
                if (connectionWasTrusted)
                {
                    AppendLog("WARN", "插件控制通道已中断；Manager 正在重新握手。", GoldBrush);
                }
                bool outcomeUnknown = result.RequestMayHaveExecuted && CheatCommands.IsMutationCommand(command);
                string message = outcomeUnknown
                    ? "作弊写命令已发送，但连续两次未能取回同一请求 ID 的结果。为避免重复执行，本窗口已冻结写操作；请关闭游戏并重新启动测试进程。"
                    : result.Error;
                AppendLog("ERROR", "作弊命令发送失败：" + message, DangerBrush);
                return new ControlResponse
                {
                    Success = false,
                    Message = message,
                    Data = new JObject { ["outcomeUnknown"] = outcomeUnknown }
                };
            }

            ControlResponse response = result.Response!;
            if (IsSessionRejection(response))
            {
                InvalidateTransportTrust();
                _hello = null;
                _status = null;
                SetConnectionState("重新握手", GoldBrush);
            }
            if (!response.Success || !string.Equals(command, CheatCommands.QueryState, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(response.Success ? "CHEAT" : "ERROR", response.Message, response.Success ? GoldBrush : DangerBrush);
            }

            if (response.Status != null)
            {
                ApplyStatus(response.Status);
            }

            return response;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "作弊命令执行失败：" + exception.Message, DangerBrush);
            return new ControlResponse { Success = false, Message = exception.Message };
        }
    }

    private bool InvalidateTransportTrust()
    {
        bool connectionWasTrusted = _sessionTrusted;
        _sessionTrusted = false;
        _automationSetupLoaded = false;
        if (_session != null) _session.ProcessInstanceId = string.Empty;
        _cheatForm?.UpdateSession(false, _hello, _status);
        SetOperationAvailability();
        return connectionWasTrusted;
    }

    private static bool IsSessionRejection(ControlResponse response) =>
        !response.Success
        && (response.Message.Contains("控制令牌无效", StringComparison.Ordinal)
            || response.Message.Contains("目标 PID", StringComparison.Ordinal)
            || response.Message.Contains("进程实例标识无效", StringComparison.Ordinal));

    private void OpenCheatForm()
    {
        if (_cheatForm == null || !_cheatForm.IsLoaded)
        {
            _cheatForm = new CheatForm(SendCheatCommandAsync);
            ConfigureIndependentToolWindow(_cheatForm);
            UiScaleService.Register(_cheatForm, _settings);
            if (_launchOptions.DemoCheatWindow && _launchOptions.WindowSize is { } size)
            {
                _cheatForm.Width = Math.Max(_cheatForm.MinWidth, size.Width);
                _cheatForm.Height = Math.Max(_cheatForm.MinHeight, size.Height);
            }
            _cheatForm.Closed += (_, _) => _cheatForm = null;
        }

        _cheatForm.UpdateSession(_sessionTrusted, _hello, _status);
        if (_launchOptions.DemoCheatWindow && _launchOptions.ScreenshotMode)
        {
            _ = _cheatForm.LoadDemoCatalogAsync();
        }
        if (!_cheatForm.IsVisible)
        {
            _cheatForm.Show();
        }

        _cheatForm.Activate();
    }

    internal static void ConfigureIndependentToolWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Owner = null;
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Topmost = false;
    }

    private AutomationRunOptions BuildRunOptions()
    {
        int speedSelection = Math.Clamp(_speed.SelectedIndex, 0, 3);
        return new AutomationRunOptions
        {
            Mode = (_mode.SelectedItem as AutomationModeOption)?.Mode ?? AutomationGameMode.Common,
            CharacterIndex = (_character.SelectedItem as AutomationCharacterOption)?.RuntimeIndex ?? 0,
            DifficultyIndex = (_character.SelectedItem as AutomationCharacterOption)?.DifficultyIndex ?? 0,
            SuperModuleIndex = (_character.SelectedItem as AutomationCharacterOption)?.SuperModuleIndex ?? 0,
            GameSpeedControlVersion = AutoPlayerGameSpeed.CurrentOptionsVersion,
            OverrideGameSpeed = ShouldOverrideGameSpeed(speedSelection),
            SpeedState = SpeedStateFromSelectionIndex(speedSelection),
            MaxRunMinutes = ParseClamped(_maxMinutes.Text, 5, 480, 120),
            ContinueExistingProfile = _continueProfile.IsChecked == true,
            SkipStory = _skipStory.IsChecked == true,
            DecisionPriority = (AutomationDecisionPriority)Math.Clamp(_decisionPriority.SelectedIndex, 0, 2)
        };
    }

    private async Task RefreshAutomationSetupAsync()
    {
        if (_session == null || !_sessionTrusted) return;
        _nextAutomationSetupQueryUtc = DateTime.UtcNow.AddSeconds(3);
        PipeCallResult call = await _pipeClient.QueryAutomationSetupAsync(_session, _lifetime.Token);
        if (!call.TransportSuccess || call.Response?.Success != true || call.Response.Data == null)
        {
            _automationSetupLoaded = false;
            return;
        }

        ApplyAutomationSetup(call.Response.Data);
    }

    private void ApplyAutomationSetup(JObject data)
    {
        AutomationGameMode selectedMode = (_mode.SelectedItem as AutomationModeOption)?.Mode ?? _settings.GameMode;
        _modeOptions.Clear();
        foreach (JObject item in (data["modes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string key = item["mode"]?.Value<string>() ?? string.Empty;
            _modeOptions.Add(new AutomationModeOption
            {
                Mode = string.Equals(key, "random", StringComparison.OrdinalIgnoreCase)
                    ? AutomationGameMode.Random
                    : AutomationGameMode.Common,
                DisplayName = item["displayName"]?.Value<string>() ?? key,
                Available = item["available"]?.Value<bool>() == true,
                Reason = item["reason"]?.Value<string>() ?? string.Empty
            });
        }

        _mode.SelectedItem = _modeOptions.FirstOrDefault(option => option.Mode == selectedMode) ??
                             _modeOptions.FirstOrDefault(option => option.Available) ?? _modeOptions.FirstOrDefault();
        _characterOptions.Clear();
        foreach (JObject item in (data["characters"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            _characterOptions.Add(new AutomationCharacterOption
            {
                CfgIndex = item["cfgIndex"]?.Value<int>() ?? -1,
                RuntimeIndex = item["runtimeIndex"]?.Value<int>() ?? 0,
                DifficultyIndex = item["difficultyIndex"]?.Value<int>() ?? 0,
                SuperModuleIndex = item["superModuleIndex"]?.Value<int>() ?? 0,
                DisplayName = item["displayName"]?.Value<string>() ?? "未命名角色"
            });
        }

        _character.SelectedItem = _characterOptions.FirstOrDefault(option => option.CfgIndex == _settings.CharacterCfgIndex) ??
                                  _characterOptions.FirstOrDefault();
        _automationSetupLoaded = true;
        UpdateCharacterVisibility();
        SetOperationAvailability();
    }

    internal static int SpeedSelectionIndex(bool overrideGameSpeed, int speedState) =>
        overrideGameSpeed ? Math.Clamp(speedState, 0, 2) + 1 : 0;

    internal static bool ShouldOverrideGameSpeed(int selectedIndex) =>
        Math.Clamp(selectedIndex, 0, 3) > 0;

    internal static int SpeedStateFromSelectionIndex(int selectedIndex) =>
        Math.Clamp(selectedIndex - 1, 0, 2);

    private bool ValidateHello(BridgeHello hello, out string error)
    {
        if (_game == null || _session == null)
        {
            error = "Manager 当前没有经过验证的有效构建会话。";
            return false;
        }

        if (hello.ProtocolVersion != Protocol.CurrentVersion)
        {
            error = $"协议不兼容：Manager v{Protocol.CurrentVersion}，插件 v{hello.ProtocolVersion}。";
            return false;
        }

        if (!Guid.TryParseExact(hello.ProcessInstanceId, "N", out _))
        {
            error = "插件握手未返回有效的游戏进程实例标识；请重装当前插件并重新启动游戏。";
            return false;
        }

        if (ShouldRejectHelloProcess(
                _session.ProcessId,
                hello.GameProcessId,
                _game.ExecutablePath))
        {
            error = $"插件来自另一个 Skyspine 进程（预期 PID {_session.ProcessId}，实际 PID {hello.GameProcessId}）。";
            return false;
        }

        if (!ValidateGameProcess(hello.GameProcessId, _game.ExecutablePath, out error))
        {
            return false;
        }

        if (_session.ProcessId == hello.GameProcessId
            && _session.ProcessStartTimeUtc.HasValue
            && !MatchesBoundGameProcess(
                hello.GameProcessId,
                _session.ProcessStartTimeUtc,
                _game.ExecutablePath))
        {
            error = "插件报告的 PID 已属于另一个游戏进程实例；Manager 将重新发现安全目标。";
            return false;
        }

        if (!string.Equals(hello.AssemblySha256, _game.AssemblySha256, StringComparison.OrdinalIgnoreCase)
            || !hello.ProductIdentityValid
            || !hello.FingerprintAccepted)
        {
            error = "插件报告的产品身份或 Assembly-CSharp SHA-256 与所选游戏不一致。";
            return false;
        }

        if (!hello.RuntimeContractAvailable)
        {
            error = "所选游戏缺少自动化运行时成员：" + string.Join(", ", hello.MissingMembers);
            return false;
        }

        if (hello.ActivationMode != _session.ActivationMode)
        {
            error = "插件回报的自动游玩模式与 Manager 本机会话不一致。";
            return false;
        }

        if (!AutoPlayerSafetyGate.IsReady(
                _session.ActivationMode,
                hello.SaveIsolationApplied,
                hello.SaveIsolationVerified,
                hello.PlatformWritesBlocked,
                hello.GameArtifactsRedirected))
        {
            error = _session.ActivationMode == AutoPlayerActivationMode.ResidentPlayer
                ? "玩家模式意外启用了 QA 隔离补丁；为保护正常存档，拒绝控制。"
                : "存档隔离、平台写入或游戏产物重定向门禁未通过，拒绝自动游玩。";
            return false;
        }

        if (!SamePath(hello.ProfileRoot, _session.Ticket.ProfileRoot)
            || !SamePath(hello.ArtifactRoot, _session.Ticket.ArtifactRoot))
        {
            error = "插件使用的本机状态目录或证据目录不属于当前控制注册。";
            return false;
        }

        if (hello.CheatSessionAuthorized != _session.Ticket.CheatModeAllowed)
        {
            error = "插件回报的作弊控制授权与当前本机控制注册不一致。";
            return false;
        }

        if (hello.CheatSessionAuthorized && hello.CheatProtocolVersion != Protocol.CheatCurrentVersion)
        {
            error = $"作弊协议不兼容：Manager v{Protocol.CheatCurrentVersion}，插件 v{hello.CheatProtocolVersion}。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool MatchesExpectedGameProcess(int? expectedProcessId, int actualProcessId) =>
        expectedProcessId is not > 0 || expectedProcessId.Value == actualProcessId;

    internal static bool ShouldRejectHelloProcess(
        int? expectedProcessId,
        int actualProcessId,
        string expectedExecutable) =>
        !MatchesExpectedGameProcess(expectedProcessId, actualProcessId)
        && IsGameProcessRunningAtPath(expectedProcessId, expectedExecutable);

    internal static bool IsGameProcessRunningAtPath(int? processId, string expectedExecutable) =>
        processId is > 0
        && ValidateGameProcess(processId.Value, expectedExecutable, out _);

    internal static bool MatchesBoundGameProcess(
        int? processId,
        DateTime? expectedStartTimeUtc,
        string expectedExecutable) =>
        processId is > 0
        && expectedStartTimeUtc.HasValue
        && TryGetGameProcessStartTimeUtc(
            processId.Value,
            expectedExecutable,
            out DateTime actualStartTimeUtc)
        && actualStartTimeUtc == expectedStartTimeUtc.Value;

    internal static bool TryGetGameProcessStartTimeUtc(
        int processId,
        string expectedExecutable,
        out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        if (processId <= 0) return false;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            string actualExecutable = process.MainModule?.FileName ?? string.Empty;
            if (!SamePath(actualExecutable, expectedExecutable)) return false;
            startTimeUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void ApplyHello(BridgeHello hello)
    {
        SetTelemetry("process", hello.GameProcessId > 0 ? $"PID {hello.GameProcessId} / 已验证" : "PID 缺失");
        SetTelemetry("gameVersion", Display(hello.GameVersion));
        SetTelemetry("pluginVersion", Display(hello.PluginVersion));
        SetTelemetry("protocol", "v" + hello.ProtocolVersion);
        SetTelemetry("unity", Display(hello.UnityVersion));
        SetTelemetry("buildGuid", ShortHash(hello.BuildGuid));
        SetTelemetry("assembly", ShortHash(hello.AssemblySha256));
        SetTelemetry("mvid", Display(hello.AssemblyMvid));
        SetTelemetry("fingerprint", hello.FingerprintAccepted ? "通过" : "拒绝");
        SetTelemetry("runtime", hello.RuntimeContractAvailable ? "完整" : "缺失成员");
        bool playerMode = hello.ActivationMode == AutoPlayerActivationMode.ResidentPlayer;
        SetTelemetry("isolation", playerMode ? "玩家原存档 / 未重定向" : hello.SaveIsolationVerified ? "已应用并验证" : "未验证");
        SetTelemetry("platform", playerMode ? "玩家模式 / 未阻断" : hello.PlatformWritesBlocked ? "已阻断" : "未阻断");
        SetTelemetry("artifacts", playerMode ? "仅记录 AutoPlayer 证据" : hello.GameArtifactsRedirected ? "已重定向" : "未重定向");
        SetTelemetry("profile", playerMode ? "当前玩家存档" : hello.ProfileRoot);
        SetTelemetry("evidence", hello.ArtifactRoot);
    }

    private void ApplyStatus(AutoPlayerStatus status)
    {
        _status = status;
        _runState.Text = RunStateName(status.RunState) + " / " + StageName(status.Stage);
        _stageDetail.Text = string.IsNullOrWhiteSpace(status.StageDetail) ? Display(status.LastMessage) : status.StageDetail;
        SetTimelineEvents(status.Timeline);
        SetTelemetry("product", Display(status.ProductName));
        SetTelemetry("gameVersion", Display(status.GameVersion));
        SetTelemetry("pluginVersion", Display(status.PluginVersion));
        SetTelemetry("protocol", "v" + status.ProtocolVersion);
        SetTelemetry("unity", Display(status.UnityVersion));
        SetTelemetry("buildGuid", ShortHash(status.BuildGuid));
        SetTelemetry("assembly", ShortHash(status.AssemblySha256));
        SetTelemetry("mvid", Display(status.AssemblyMvid));
        SetTelemetry("fingerprint", status.FingerprintAccepted ? "通过" : "拒绝");
        SetTelemetry("runtime", status.RuntimeContractAvailable ? "完整" : "缺失成员");
        bool playerMode = status.ActivationMode == AutoPlayerActivationMode.ResidentPlayer;
        SetTelemetry("isolation", playerMode ? "玩家原存档 / 未重定向" : status.SaveIsolationVerified ? "已应用并验证" : "未验证");
        SetTelemetry("platform", playerMode ? "玩家模式 / 未阻断" : status.PlatformWritesBlocked ? "已阻断" : "未阻断");
        SetTelemetry("artifacts", playerMode ? "仅记录 AutoPlayer 证据" : status.GameArtifactsRedirected ? "已重定向" : "未重定向");
        SetTelemetry("profile", playerMode ? "当前玩家存档" : status.IsolatedSaveRoot);
        SetTelemetry("evidence", string.IsNullOrWhiteSpace(status.EvidenceDirectory) ? status.ArtifactDirectory : status.EvidenceDirectory);
        SetTelemetry(
            "integrity",
            status.CheatUsed
                ? playerMode
                    ? "当前存档存在作弊记录"
                    : "隔离 QA 档存在作弊记录"
                : playerMode ? "玩家模式 / 未使用作弊" : "隔离 QA / 未使用作弊");
        SetTelemetry("outcome", OutcomeName(status.Outcome));
        SetTelemetry("waves", $"{status.WavesCompleted} 完成 / {status.WavesStarted} 启动");
        SetTelemetry(
            "chapter",
            status.CurrentChapter > 0
                ? $"第 {status.CurrentChapter} 章 / 第 {status.CurrentMapLayer} 层"
                : "等待地图数据");
        SetTelemetry(
            "frameTiming",
            status.FrameSampleCount > 0
                ? $"{status.CurrentFps:F1} FPS / 1% Low {status.OnePercentLowFps:F1} / P99 {status.FrameTimeP99Ms:F1} ms / {status.FrameTelemetryWindowSeconds:F1} 秒"
                : "等待采样");
        SetTelemetry(
            "runtimeTiming",
            string.IsNullOrWhiteSpace(status.LastRuntimeCommand)
                ? "等待采样"
                : $"{status.LastRuntimeCommand} {status.LastRuntimeCommandDurationMs:F1} ms / 峰值 " +
                  $"{(string.IsNullOrWhiteSpace(status.MaxRuntimeCommand) ? "未知命令" : status.MaxRuntimeCommand)} " +
                  $"{status.MaxRuntimeCommandDurationMs:F1} ms / 慢调用 {status.SlowRuntimeCommandCount}");
        int processId = _hello?.GameProcessId ?? _session?.ProcessId ?? 0;
        string processPrefix = processId > 0 ? $"PID {processId} / " : string.Empty;
        SetTelemetry(
            "process",
            processPrefix + (status.NeedsProcessRestart
                ? "必须彻底重启"
                : status.CheatUsed ? "可自动游玩 / 已标记作弊" : "可随时控制"));

        string signature = $"{status.RunState}|{status.Outcome}|{status.Stage}|{status.LastCommand}|{status.LastMessage}|{status.NeedsProcessRestart}|{status.CheatModeEnabled}|{status.CheatUsed}|{status.CurrentMapStage}|{status.CurrentMapLayer}";
        if (!string.Equals(signature, _lastStatusSignature, StringComparison.Ordinal))
        {
            _lastStatusSignature = signature;
            AppendLog("STATE", $"{RunStateName(status.RunState)} / {StageName(status.Stage)} / {status.LastMessage}", BlueBrush);
        }

        bool isolationTrusted = _session != null
                                && status.ActivationMode == _session.ActivationMode
                                && AutoPlayerSafetyGate.IsReady(
                                    status.ActivationMode,
                                    status.SaveIsolationApplied,
                                    status.SaveIsolationVerified,
                                    status.PlatformWritesBlocked,
                                    status.GameArtifactsRedirected);
        bool statusTrusted = status.ProductIdentityValid
                              && status.FingerprintAccepted
                              && status.RuntimeContractAvailable
                              && isolationTrusted
                             && (_game == null || string.Equals(
                                 status.AssemblySha256,
                                 _game.AssemblySha256,
                                 StringComparison.OrdinalIgnoreCase))
                              && (_session == null
                                  || SamePath(status.ArtifactDirectory, _session.Ticket.ArtifactRoot))
                              && (_session == null
                                  || _session.ActivationMode == AutoPlayerActivationMode.ResidentPlayer
                                  || SamePath(status.IsolatedSaveRoot, _session.Ticket.ProfileRoot));
        if (!statusTrusted)
        {
            InvalidateTransportTrust();
            SetConnectionState("门禁失败", DangerBrush);
        }
        else if (_sessionTrusted)
        {
            SetConnectionState(StatusBadgeText(status.RunState), BrushForRunState(status.RunState));
        }
        else
        {
            SetConnectionState("等待门禁", GoldBrush);
        }

        bool autoPlayActive = status.RunState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused;
        if (status.NeedsProcessRestart)
        {
            ShowRestartRequired();
            if (!_restartWarningReported)
            {
                _restartWarningReported = true;
                AppendLog(
                    "WARN",
                    "插件要求重启游戏进程。请彻底关闭 Skyspine，再由 Manager 重新启动；当前进程禁止开始新一轮自动游玩。",
                    GoldBrush);
            }
        }
        else if (status.CheatModeEnabled && status.RunState == AutoPlayerRunState.Standby)
        {
            _restartWarningReported = false;
            _runState.Text = "作弊模式 / 已启用";
            _stageDetail.Text = status.CheatUsed
                ? "当前存档存在作弊记录；关闭持续效果后仍可开始自动游玩。"
                : "作弊工具已就绪；怪物 ID 与 Buff 监视不会改动对局。";
            SetStageVisual(GoldBrush, WarningStageBackground);
            SetConnectionState("作弊模式", GoldBrush);
            SetTelemetry("process", status.CheatUsed ? "作弊模式 / 已记录修改" : "作弊模式已启用");
            _cheatMarkerReported = false;
        }
        else
        {
            _restartWarningReported = false;
            if (status.CheatUsed && !_cheatMarkerReported)
            {
                _cheatMarkerReported = true;
                AppendLog(
                    "INFO",
                    "作弊模式已关闭；本次运行证据会继续标记为 cheat-modified。",
                    GoldBrush);
            }
            else if (!status.CheatUsed)
            {
                _cheatMarkerReported = false;
            }

            SetStageVisual(status.CheatUsed ? GoldBrush : SignalBrush, NormalStageBackground);
            if (autoPlayActive && status.CheatModeEnabled)
            {
                SetTelemetry("process", processPrefix + "自动游玩 / 怪物监视");
            }
        }

        _cheatForm?.UpdateSession(_sessionTrusted, _hello, status);
        SetOperationAvailability();
    }

    private void SetTimelineEvents(IReadOnlyList<TimelineEvent>? events)
    {
        IReadOnlyList<TimelineEvent> history = TimelineHistory(events);
        string signature = string.Join(
            '\u001e',
            history.Select(item => $"{item.TimestampUtc.Ticks}|{item.Stage}|{item.Kind}|{item.Message}"));
        if (string.Equals(signature, _timelineSignature, StringComparison.Ordinal)) return;

        bool followLatest = _timeline.Count == 0
                            || _timelineScroll.ScrollableHeight <= 0
                            || _timelineScroll.VerticalOffset >= _timelineScroll.ScrollableHeight - 2;
        double previousOffset = _timelineScroll.VerticalOffset;
        _timelineSignature = signature;
        _timeline.Clear();
        foreach (TimelineEvent item in history)
        {
            string time = item.TimestampUtc == default
                ? "--:--:--"
                : item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            _timeline.Add(new TimelineDisplayItem(
                $"{time}  {StageName(item.Stage)}",
                Display(item.Message),
                BrushForTimelineKind(item.Kind)));
        }

        _timelineEmpty.Visibility = _timeline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _timelineItems.Visibility = _timeline.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (followLatest)
                {
                    _timelineScroll.ScrollToEnd();
                    return;
                }

                _timelineScroll.ScrollToVerticalOffset(Math.Min(previousOffset, _timelineScroll.ScrollableHeight));
            }));
    }

    internal static IReadOnlyList<TimelineEvent> TimelineHistory(IReadOnlyList<TimelineEvent>? events) =>
        events?.ToArray() ?? Array.Empty<TimelineEvent>();

    private void ApplyBuildTelemetry(GameInstallValidation game)
    {
        SetTelemetry("product", Display(game.ProductName));
        SetTelemetry("gameVersion", Display(game.ProductVersion));
        SetTelemetry("assembly", ShortHash(game.AssemblySha256));
        SetTelemetry("mvid", Display(game.AssemblyMvid));
        SetTelemetry("fingerprint", "待插件握手");
    }

    private void ClearBuildTelemetry()
    {
        foreach (string key in _telemetry.Keys)
        {
            SetTelemetry(key, "-");
        }
    }

    private void RefreshPluginStatus()
    {
        if (_game == null)
        {
            _pluginStatus = null;
            _pluginState.Text = "插件状态：未知";
            return;
        }

        _pluginStatus = _installer.GetStatus(_game.GameRoot);
        string bepinex = _pluginStatus.BepInExPresent ? "BepInEx 已就绪" : "BepInEx 未安装";
        _pluginState.Text = _pluginStatus.State switch
        {
            PluginState.Enabled => $"插件已启用  {Display(_pluginStatus.PluginVersion)}",
            PluginState.Disabled => $"插件已停用  {Display(_pluginStatus.PluginVersion)}",
            PluginState.Incomplete => "插件安装不完整",
            _ => bepinex
        };
        _pluginState.Foreground = _pluginStatus.State switch
        {
            PluginState.Enabled => SignalBrush,
            PluginState.Disabled => GoldBrush,
            PluginState.Incomplete => DangerBrush,
            _ => MutedBrush
        };
        SetTelemetry("pluginVersion", Display(_pluginStatus.PluginVersion));
        SetOperationAvailability();
    }

    private void SetOperationAvailability()
    {
        if (_launchOptions.DemoMode)
        {
            SetInteractiveControls(false);
            SetControlButtons(_sessionTrusted);
            return;
        }

        bool validGame = _game?.IsValid == true;
        _installButton.IsEnabled = validGame;
        _togglePluginButton.IsEnabled = validGame && _pluginStatus?.State is PluginState.Enabled or PluginState.Disabled;
        _togglePluginButton.Content = _pluginStatus?.State == PluginState.Disabled ? "启用" : "停用";
        _uninstallButton.IsEnabled = validGame && _pluginStatus?.State != PluginState.NotInstalled;
        _launchButton.IsEnabled = validGame
                                  && _pluginStatus?.State == PluginState.Enabled
                                  && !_sessionTrusted;
        _continueProfile.IsEnabled = validGame;
        _cheatButton.IsEnabled = _sessionTrusted
                                 && (_status?.CheatSessionAuthorized == true
                                     || _hello?.CheatSessionAuthorized == true
                                     || _status?.CheatAvailable == true
                                     || _hello?.CheatAvailable == true);
        _openEvidenceButton.IsEnabled = !string.IsNullOrWhiteSpace(EvidenceDirectory());
        SetControlButtons(_sessionTrusted);
    }

    private void SetControlButtons(bool enabled)
    {
        RunControlAvailability availability = RunControlAvailability.From(enabled, _status);
        bool setupAllowsStart = _launchOptions.DemoMode || _continueProfile.IsChecked == true ||
                                (_automationSetupLoaded &&
                                 _mode.SelectedItem is AutomationModeOption { Available: true } mode &&
                                 (mode.Mode == AutomationGameMode.Random || _character.SelectedItem != null));
        _startButton.IsEnabled = availability.CanStart && setupAllowsStart;
        _pauseButton.IsEnabled = availability.CanPause;
        _resumeButton.IsEnabled = availability.CanResume;
        _stopButton.IsEnabled = availability.CanStop;
    }

    private void ShowRestartRequired()
    {
        SetStageState(
            "必须重启 / 当前进程不可继续",
            "彻底关闭 Skyspine 游戏进程后，再由 Manager 重新启动；当前进程不能开始新一轮自动游玩。",
            DangerBrush,
            RestartStageBackground);
        SetConnectionState("需要重启", DangerBrush);
        SetTelemetry("process", "必须彻底重启");
        SetControlButtons(_sessionTrusted);
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? Cursors.Wait : null;
        _browseButton.IsEnabled = !busy;
        _continueProfile.IsEnabled = !busy;
        if (busy)
        {
            _installButton.IsEnabled = false;
            _togglePluginButton.IsEnabled = false;
            _uninstallButton.IsEnabled = false;
            _launchButton.IsEnabled = false;
        }
    }

    private void SetInteractiveControls(bool enabled)
    {
        foreach (UIElement control in new UIElement[]
                 {
                     _browseButton, _installButton, _togglePluginButton, _uninstallButton, _launchButton,
                     _cheatButton, _startButton, _pauseButton, _resumeButton, _stopButton, _updateButton
                 })
        {
            control.IsEnabled = enabled;
        }
    }

    private void CheckSelectedProcessBoundary()
    {
        if (_session?.ProcessId is not > 0 || _transportFailures < 5) return;

        try
        {
            using Process process = Process.GetProcessById(_session.ProcessId.Value);
            if (!process.HasExited) return;
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }

        AppendLog(
            "ERROR",
            "已连接的 Skyspine 游戏已经退出。Manager 会继续在后台等待同一受信游戏再次启动。",
            DangerBrush);
        _sessionTrusted = false;
        _hello = null;
        _status = null;
        _session.ProcessId = null;
        _session.ProcessStartTimeUtc = null;
        _session.ProcessInstanceId = string.Empty;
        _lastStatusSignature = string.Empty;
        SetConnectionState("等待游戏", GoldBrush);
        SetOperationAvailability();
    }

    private void ReadPlayerLog()
    {
        try
        {
            foreach (string line in _logTail.ReadAvailable())
            {
                Brush color = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                              || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                              || line.Contains("错误", StringComparison.Ordinal)
                              || line.Contains("异常", StringComparison.Ordinal)
                    ? DangerBrush
                    : line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("警告", StringComparison.Ordinal)
                        ? GoldBrush
                        : TextBrush;
                AppendLog("GAME", line, color);
            }
        }
        catch (Exception exception)
        {
            AppendLog("WARN", "Player.log 暂时无法读取：" + exception.Message, GoldBrush);
        }
    }

    private async Task UpdateButtonOnClickAsync()
    {
        if (_launchOptions.DemoMode) return;
        SaveSettings();
        if (_updateAvailable)
        {
            UpdateConfirmationDialog confirmation = new(
                ManagerProductInfo.Version,
                _latestUpdateVersion)
            {
                Owner = this
            };
            UiScaleService.Register(confirmation, _settings);
            if (confirmation.ShowDialog() != true) return;
            (bool success, string message) = _updates.StartApply(_settings, _session?.ProcessId);
            AppendLog(success ? "INFO" : "ERROR", message, success ? BlueBrush : DangerBrush);
            if (success)
            {
                Close();
            }

            return;
        }

        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        _updateButton.IsEnabled = false;
        _updateState.Text = "正在检查...";
        ManagerUpdateStatus result = await _updates.CheckAsync(_settings, _lifetime.Token);
        _updateButton.IsEnabled = true;
        _updateAvailable = result.Success && result.UpdateAvailable;
        _latestUpdateVersion = _updateAvailable ? result.LatestVersion : string.Empty;
        _updateState.Text = result.UpdateAvailable
            ? $"可更新 {result.LatestVersion}"
            : result.Success ? "当前已是最新版本" : "更新检查不可用";
        _updateButton.Content = result.UpdateAvailable ? "安装更新" : "检查更新";
        if (userInitiated || result.UpdateAvailable)
        {
            AppendLog(result.Success ? "INFO" : "WARN", result.Message, result.Success ? BlueBrush : GoldBrush);
        }
    }

    private void ApplyDemoMode()
    {
        _game = DemoData.Game();
        _hello = _launchOptions.DemoCheatWindow ? DemoData.CheatHello() : DemoData.Hello();
        _status = _launchOptions.DemoCheatWindow
            ? DemoData.CheatStatus()
            : DemoData.Status(_launchOptions.DemoRestartRequired);
        _gamePath.Text = _game.GameRoot;
        _validationState.Text = "已验证 Skyspine 1.237 / " + ShortHash(_game.AssemblySha256);
        _validationState.Foreground = SignalBrush;
        _pluginState.Text = "插件已启用  " + _hello.PluginVersion;
        _pluginState.Foreground = SignalBrush;
        _sessionTrusted = true;
        SetConnectionState("安全连接", SignalBrush);
        ApplyBuildTelemetry(_game);
        ApplyHello(_hello);
        ApplyStatus(_status);
        _updateState.Text = "演示数据";
        foreach (string line in DemoData.LogLines())
        {
            AppendLog(string.Empty, line, line.Contains("安全", StringComparison.Ordinal) ? SignalBrush : TextBrush);
        }

        _openEvidenceButton.IsEnabled = false;
    }

    private async Task CaptureScreenshotAsync()
    {
        await Task.Delay(450);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        string output = string.IsNullOrWhiteSpace(_launchOptions.ScreenshotOutput)
            ? Path.Combine(Protocol.DataRoot, "artifacts", "manager-screenshot.png")
            : _launchOptions.ScreenshotOutput;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        FrameworkElement captureTarget = _launchOptions.DemoCheatWindow && _cheatForm is { IsLoaded: true }
            ? (_cheatForm.Content as FrameworkElement ?? _cheatForm)
            : _captureSurface;
        captureTarget.UpdateLayout();
        double width = Math.Max(1, captureTarget.ActualWidth);
        double height = Math.Max(1, captureTarget.ActualHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(captureTarget);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
        RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(captureTarget);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = new(output, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        if (_launchOptions.ExitAfterScreenshot)
        {
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void OpenEvidenceDirectory()
    {
        string directory = EvidenceDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            AppendLog("WARN", "证据目录尚未创建。", GoldBrush);
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "无法打开证据目录：" + exception.Message, DangerBrush);
        }
    }

    private string EvidenceDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_status?.EvidenceDirectory)) return _status.EvidenceDirectory;
        if (!string.IsNullOrWhiteSpace(_status?.ArtifactDirectory)) return _status.ArtifactDirectory;
        return _session?.Ticket.ArtifactRoot ?? string.Empty;
    }

    private void AppendOperation(PluginOperationResult result)
    {
        AppendLog(result.Success ? "INFO" : "ERROR", result.Message, result.Success ? SignalBrush : DangerBrush);
    }

    private void AppendLog(string category, string message, Brush color)
    {
        if (string.IsNullOrWhiteSpace(message) || _lifetime.IsCancellationRequested) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(category, message, color)));
            return;
        }

        bool shouldFollowLatest = ShouldFollowLatest(
            _logs.VerticalOffset,
            _logs.ViewportHeight,
            _logs.ExtentHeight);
        string prefix = string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : DateTime.Now.ToString("HH:mm:ss.fff") + "  " + LogCategoryName(category).PadRight(4) + " ";
        Paragraph paragraph = new(new Run(prefix + message.Replace("\r", string.Empty).Replace("\n", " "))
        {
            Foreground = color
        })
        {
            Margin = new Thickness(0),
            LineHeight = 18
        };
        _logs.Document.Blocks.Add(paragraph);
        while (_logs.Document.Blocks.Count > 1500)
        {
            while (_logs.Document.Blocks.Count > 1000 && _logs.Document.Blocks.FirstBlock is { } first)
            {
                _logs.Document.Blocks.Remove(first);
            }
        }

        if (shouldFollowLatest) _logs.ScrollToEnd();
    }

    internal static bool ShouldFollowLatest(
        double verticalOffset,
        double viewportHeight,
        double extentHeight,
        double tolerance = 2) =>
        extentHeight <= viewportHeight ||
        verticalOffset + viewportHeight >= extentHeight - Math.Max(0, tolerance);

    internal static string LogCategoryName(string category) => category.ToUpperInvariant() switch
    {
        "INFO" => "信息",
        "WARN" => "警告",
        "ERROR" => "错误",
        "SAFE" => "安全",
        "ACT" => "操作",
        "STATE" => "状态",
        "GAME" => "游戏",
        "CHEAT" => "作弊",
        _ => category
    };

    internal static string ControlCommandName(string command) => command.ToLowerInvariant() switch
    {
        "start" => "开始命令",
        "pause" => "暂停命令",
        "resume" => "继续命令",
        "stop" => "停止命令",
        _ => "控制命令"
    };

    private void SaveSettings()
    {
        if (_launchOptions.DemoMode) return;
        _settings.GameRoot = _game?.GameRoot ?? _gamePath.Text.Trim();
        _settings.ProfileName = string.IsNullOrWhiteSpace(_profileName.Text) ? "player-default" : _profileName.Text.Trim();
        _settings.ContinueExistingProfile = _continueProfile.IsChecked == true;
        _settings.GameMode = (_mode.SelectedItem as AutomationModeOption)?.Mode ?? AutomationGameMode.Common;
        _settings.CharacterCfgIndex = (_character.SelectedItem as AutomationCharacterOption)?.CfgIndex ?? -1;
        int speedSelection = Math.Clamp(_speed.SelectedIndex, 0, 3);
        _settings.OverrideGameSpeed = ShouldOverrideGameSpeed(speedSelection);
        _settings.SpeedState = SpeedStateFromSelectionIndex(speedSelection);
        _settings.MaxRunMinutes = ParseClamped(_maxMinutes.Text, 5, 480, 120);
        _settings.SkipStory = _skipStory.IsChecked == true;
        _settings.DecisionPriority = (AutomationDecisionPriority)Math.Clamp(_decisionPriority.SelectedIndex, 0, 2);
        _settings.UiScaleMode = _uiScaleMode.SelectedIndex == 1 ? UiScaleMode.Custom : UiScaleMode.System;
        _settings.CustomUiScalePercent = 75 + Math.Max(0, _uiScalePercent.SelectedIndex) * 5;
        _settings.NormalizeUpdateSource();
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            AppendLog("WARN", "Manager 设置无法保存：" + exception.Message, GoldBrush);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        SaveSettings();
        _pollTimer.Stop();
        _lifetime.Cancel();
        _cheatForm?.Close();
        _session?.DeleteTicket();
    }

    private async void PollTimerOnTick(object? sender, EventArgs eventArgs)
    {
        await PollPluginAsync();
    }

    private void SetTelemetry(string key, string value)
    {
        if (!_telemetry.TryGetValue(key, out TelemetryRow? row)) return;
        row.Value = Display(value);
    }

    private void SetConnectionState(string text, Brush color)
    {
        _connectionText.Text = text;
        _connectionLamp.Fill = color;
    }

    private void SetStageState(string title, string detail, Brush color, Brush background)
    {
        _runState.Text = title;
        _stageDetail.Text = detail;
        SetStageVisual(color, background);
    }

    private void SetStageVisual(Brush color, Brush background)
    {
        _stageBanner.Background = background;
        _runState.Foreground = color;
        _stageLamp.Fill = color;
    }

    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private Brush BrushForRunState(AutoPlayerRunState state) => state switch
    {
        AutoPlayerRunState.Running => SignalBrush,
        AutoPlayerRunState.Paused => GoldBrush,
        AutoPlayerRunState.Completed => BlueBrush,
        AutoPlayerRunState.Faulted or AutoPlayerRunState.Incompatible => DangerBrush,
        _ => SignalBrush
    };

    private Brush BrushForTimelineKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "error" or "fault" => DangerBrush,
        "warning" => GoldBrush,
        "complete" => BlueBrush,
        "command" or "action" => SignalBrush,
        _ => MutedBrush
    };

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static int ParseClamped(string? text, int minimum, int maximum, int fallback)
    {
        return int.TryParse(text, out int value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value.Length <= 18 ? value : value[..10] + "..." + value[^6..];
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ValidateGameProcess(int processId, string expectedExecutable, out string error)
    {
        if (processId <= 0)
        {
            error = "插件握手未返回有效的游戏进程 PID；请重装当前插件并重新启动游戏。";
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                error = $"插件报告的游戏进程 PID {processId} 已退出。";
                return false;
            }

            string actualExecutable = process.MainModule?.FileName ?? string.Empty;
            if (!SamePath(actualExecutable, expectedExecutable))
            {
                error = $"插件进程 PID {processId} 不属于当前选择的游戏目录，拒绝建立控制通道。";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = $"无法验证插件报告的游戏进程 PID {processId}：{exception.Message}";
            return false;
        }
    }

    private static string RunStateName(AutoPlayerRunState state) => state switch
    {
        AutoPlayerRunState.Standby => "待机",
        AutoPlayerRunState.Running => "运行中",
        AutoPlayerRunState.Paused => "已暂停",
        AutoPlayerRunState.Completed => "已完成",
        AutoPlayerRunState.Faulted => "故障",
        AutoPlayerRunState.Incompatible => "不兼容",
        _ => state.ToString()
    };

    private static string OutcomeName(AutomationOutcome outcome) => outcome switch
    {
        AutomationOutcome.Unknown => "尚未开始",
        AutomationOutcome.InProgress => "进行中",
        AutomationOutcome.Victory => "胜利",
        AutomationOutcome.Defeat => "失败",
        AutomationOutcome.Timeout => "超时未胜利",
        AutomationOutcome.WaveLimit => "达到波次上限",
        AutomationOutcome.Stopped => "已停止",
        AutomationOutcome.Error => "运行错误",
        _ => outcome.ToString()
    };

    private static string StatusBadgeText(AutoPlayerRunState state) => state switch
    {
        AutoPlayerRunState.Running => "自动游玩中",
        AutoPlayerRunState.Paused => "已暂停",
        AutoPlayerRunState.Completed => "已完成",
        AutoPlayerRunState.Faulted => "运行故障",
        AutoPlayerRunState.Incompatible => "不兼容",
        _ => "安全连接"
    };

    private static string StageName(AutomationStage stage) => stage switch
    {
        AutomationStage.WaitingForGame => "等待游戏",
        AutomationStage.FrontEnd => "主菜单",
        AutomationStage.RandomSelection => "随机模式选择",
        AutomationStage.InitializingRun => "初始化对局",
        AutomationStage.PreparingDefense => "准备防线",
        AutomationStage.ManagingRewards => "处理奖励",
        AutomationStage.ManagingEvent => "处理事件",
        AutomationStage.ManagingShop => "处理商店",
        AutomationStage.SelectingRoute => "选择路线",
        AutomationStage.StartingWave => "启动波次",
        AutomationStage.Battle => "战斗",
        AutomationStage.Completed => "完成",
        AutomationStage.Recovery => "恢复",
        _ => stage.ToString()
    };

    private sealed class TelemetryRow : INotifyPropertyChanged
    {
        private string _value = "-";

        public TelemetryRow(string key, string caption)
        {
            Key = key;
            Caption = caption;
        }

        public string Key { get; }
        public string Caption { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal)) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record TimelineDisplayItem(string Heading, string Message, Brush Accent);
}
