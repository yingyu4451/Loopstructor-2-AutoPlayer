using System.Diagnostics;
using System.Drawing.Imaging;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed class MainForm : Form
{
    private readonly ManagerLaunchOptions _launchOptions;
    private readonly ManagerSettingsStore _settingsStore;
    private readonly DistributionLayout _distribution;
    private readonly GameInstallValidator _validator = new();
    private readonly BepInExConfigWriter _configWriter = new();
    private readonly ActivationSessionFactory _sessionFactory = new();
    private readonly PipeControlClient _pipeClient = new();
    private readonly LogTailReader _logTail = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 850 };
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, Label> _telemetry = new(StringComparer.Ordinal);

    private ManagerSettings _settings;
    private BepInExInstaller _installer;
    private GameLauncher _gameLauncher;
    private UpdateCoordinator _updates;
    private GameInstallValidation? _game;
    private ActivationSession? _session;
    private PluginInstallStatus? _pluginStatus;
    private BridgeHello? _hello;
    private AutoPlayerStatus? _status;
    private bool _pollInProgress;
    private bool _sessionTrusted;
    private bool _legacyProbeDone;
    private bool _updateAvailable;
    private int _transportFailures;
    private string _lastStatusSignature = string.Empty;
    private string _lastTrustError = string.Empty;
    private bool _restartWarningReported;

    private TextBox _gamePath = null!;
    private Label _validationState = null!;
    private Label _pluginState = null!;
    private Button _browseButton = null!;
    private Button _installButton = null!;
    private Button _togglePluginButton = null!;
    private Button _uninstallButton = null!;
    private Button _launchButton = null!;
    private Button _cheatButton = null!;
    private TextBox _profileName = null!;
    private CheckBox _continueProfile = null!;
    private CheckBox _autoUpdateCheck = null!;
    private ComboBox _mode = null!;
    private NumericUpDown _speed = null!;
    private NumericUpDown _maxMinutes = null!;
    private Button _startButton = null!;
    private Button _pauseButton = null!;
    private Button _resumeButton = null!;
    private Button _stopButton = null!;
    private Label _runState = null!;
    private Label _stageDetail = null!;
    private Panel _stageBanner = null!;
    private TimelineControl _timeline = null!;
    private ConnectionBadge _connection = null!;
    private Button _updateButton = null!;
    private Label _updateState = null!;
    private Label _productVersion = null!;
    private RichTextBox _logs = null!;
    private Button _openEvidenceButton = null!;
    private Panel _captureSurface = null!;
    private CheatForm? _cheatForm;

    public MainForm(ManagerLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        _settingsStore = new ManagerSettingsStore();
        _settings = _settingsStore.Load(out string settingsWarning);
        _distribution = DistributionLayout.Locate();
        _installer = new BepInExInstaller(_distribution, _configWriter);
        _gameLauncher = new GameLauncher(_sessionFactory, _configWriter);
        _updates = new UpdateCoordinator(_distribution);

        InitializeWindow();
        BuildInterface();
        BindSettings();
        SetOperationAvailability();
        _pollTimer.Tick += PollTimerOnTick;
        Shown += async (_, _) => await OnShownAsync(settingsWarning);
        FormClosing += OnFormClosing;
    }

    private void InitializeWindow()
    {
        Text = $"Loopstructor 2.AutoPlayer Manager — v{ManagerProductInfo.Version}";
        BackColor = Theme.Canvas;
        ForeColor = Theme.Ink;
        Font = Theme.Body(9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = _launchOptions.WindowSize ?? new Size(1400, 860);
        MinimumSize = new Size(1100, 680);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
    }

    private void BuildInterface()
    {
        _captureSurface = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas
        };
        _captureSurface.Controls.Add(BuildWorkspace());
        _captureSurface.Controls.Add(BuildHeader());
        Controls.Add(_captureSurface);
    }

    private Control BuildHeader()
    {
        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = Theme.Ink,
            Padding = new Padding(22, 10, 18, 10)
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));

        Panel identity = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        Label title = new()
        {
            Text = "SKYSPINE  /  QA 自动游玩",
            ForeColor = Color.White,
            Font = Theme.Display(16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 1)
        };
        FlowLayoutPanel productMeta = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Location = new Point(2, 31),
            Margin = Padding.Empty
        };
        _productVersion = new Label
        {
            Name = "ProductVersionLabel",
            AccessibleName = "AutoPlayer 版本",
            Text = ManagerProductInfo.DisplayText,
            ForeColor = Color.White,
            Font = Theme.Data(8.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 14, 0)
        };
        Label subtitle = new()
        {
            Text = "构建验证、隔离运行与自动游玩控制",
            ForeColor = Color.FromArgb(177, 191, 199),
            Font = Theme.Body(8.5f),
            AutoSize = true,
            Margin = Padding.Empty
        };
        productMeta.Controls.Add(_productVersion);
        productMeta.Controls.Add(subtitle);
        identity.Controls.Add(title);
        identity.Controls.Add(productMeta);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 9, 0, 0)
        };
        _connection = new ConnectionBadge { Margin = new Padding(10, 0, 0, 0) };
        _updateButton = Theme.CommandButton("检查更新", Theme.Blue, 96);
        _updateButton.Height = 30;
        _updateButton.Margin = new Padding(10, 0, 0, 0);
        _updateButton.Click += async (_, _) => await UpdateButtonOnClickAsync();
        _updateState = new Label
        {
            Text = "尚未检查更新",
            ForeColor = Color.FromArgb(187, 199, 205),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            Width = 170,
            Height = 30,
            Font = Theme.Data(8.5f)
        };
        actions.Controls.Add(_connection);
        actions.Controls.Add(_updateButton);
        actions.Controls.Add(_updateState);
        layout.Controls.Add(identity, 0, 0);
        layout.Controls.Add(actions, 1, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Control BuildWorkspace()
    {
        TableLayoutPanel workspace = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Canvas
        };
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyle logRow = new(SizeType.Absolute, 232);
        workspace.RowStyles.Add(logRow);
        workspace.Resize += (_, _) =>
            logRow.Height = Math.Clamp((int)(workspace.ClientSize.Height * 0.34f), 210, 250);

        TableLayoutPanel main = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Theme.Canvas
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 318));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));

        Control targetPanel = BuildTargetPanel();
        targetPanel.Margin = new Padding(0, 0, 12, 0);
        Control runPanel = BuildRunPanel();
        runPanel.Margin = new Padding(0, 0, 12, 0);
        Control telemetryPanel = BuildTelemetryPanel();
        telemetryPanel.Margin = Padding.Empty;
        main.Controls.Add(targetPanel, 0, 0);
        main.Controls.Add(runPanel, 1, 0);
        main.Controls.Add(telemetryPanel, 2, 0);

        workspace.Controls.Add(main, 0, 0);
        workspace.Controls.Add(BuildLogPanel(), 0, 1);
        return workspace;
    }

    private Control BuildTargetPanel()
    {
        SectionPanel section = new() { Dock = DockStyle.Fill };
        Label heading = SectionHeading("测试目标");
        heading.Dock = DockStyle.Top;
        section.Controls.Add(heading);

        FlowLayoutPanel fields = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 12, 0, 0)
        };
        section.Controls.Add(fields);
        fields.BringToFront();

        Panel pathField = FieldPanel("Skyspine 打包目录", 62);
        _gamePath = InputBox();
        _gamePath.ReadOnly = true;
        _gamePath.Location = new Point(0, 23);
        _gamePath.Width = 184;
        _browseButton = Theme.CommandButton("选择", Theme.Blue, 60);
        _browseButton.Location = new Point(190, 22);
        _browseButton.Height = 31;
        _browseButton.Click += async (_, _) => await BrowseForGameAsync();
        pathField.Controls.Add(_gamePath);
        pathField.Controls.Add(_browseButton);
        fields.Controls.Add(pathField);

        _validationState = new Label
        {
            Text = "尚未选择构建",
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.5f),
            Width = 250,
            Height = 32,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 4)
        };
        fields.Controls.Add(_validationState);
        fields.Controls.Add(Divider());

        _pluginState = new Label
        {
            Text = "插件状态：未知",
            ForeColor = Theme.Ink,
            Font = Theme.Body(9f, FontStyle.Bold),
            Width = 250,
            Height = 24,
            AutoEllipsis = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        fields.Controls.Add(_pluginState);

        Panel pluginActions = new() { Width = 250, Height = 36, Margin = new Padding(0, 0, 0, 7) };
        _installButton = Theme.CommandButton("安装", Theme.Teal, 76);
        _installButton.Location = new Point(0, 0);
        _installButton.Click += async (_, _) => await InstallPluginAsync();
        _togglePluginButton = Theme.CommandButton("停用", Theme.Amber, 76);
        _togglePluginButton.Location = new Point(87, 0);
        _togglePluginButton.Click += (_, _) => TogglePlugin();
        _uninstallButton = Theme.CommandButton("卸载", Theme.Red, 76);
        _uninstallButton.Location = new Point(174, 0);
        _uninstallButton.Click += (_, _) => UninstallPlugin();
        pluginActions.Controls.AddRange(new Control[] { _installButton, _togglePluginButton, _uninstallButton });
        fields.Controls.Add(pluginActions);

        _profileName = InputBox();
        fields.Controls.Add(FieldPanel("隔离配置名称", _profileName));
        _continueProfile = new CheckBox
        {
            Text = "继续隔离档中的未完成对局",
            Width = 250,
            Height = 24,
            ForeColor = Theme.Ink,
            Font = Theme.Body(8.5f),
            Margin = new Padding(0, 0, 0, 5)
        };
        fields.Controls.Add(_continueProfile);

        _launchButton = Theme.CommandButton("启动所选测试包", Theme.TealDark, 250);
        _launchButton.Height = 37;
        _launchButton.Margin = new Padding(0, 0, 0, 10);
        _launchButton.Click += (_, _) => LaunchGame();
        fields.Controls.Add(_launchButton);

        _cheatButton = Theme.CommandButton("作弊工具", Theme.Amber, 250);
        _cheatButton.Height = 35;
        _cheatButton.Margin = new Padding(0, 0, 0, 10);
        _cheatButton.AccessibleName = "打开作弊工具";
        _cheatButton.Click += (_, _) => OpenCheatForm();
        fields.Controls.Add(_cheatButton);
        fields.Controls.Add(Divider());

        _autoUpdateCheck = new CheckBox
        {
            Text = "启动 Manager 时检查更新",
            Width = 250,
            Height = 28,
            ForeColor = Theme.Ink,
            Font = Theme.Body(8.5f),
            Margin = new Padding(0, 0, 0, 8)
        };
        fields.Controls.Add(_autoUpdateCheck);

        return section;
    }

    private Control BuildRunPanel()
    {
        SectionPanel section = new() { Dock = DockStyle.Fill };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(SectionHeading("运行轨道"), 0, 0);
        FlowLayoutPanel commands = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 4)
        };
        _startButton = Theme.CommandButton("开始", Theme.Teal, 86);
        _pauseButton = Theme.CommandButton("暂停", Theme.Amber, 78);
        _resumeButton = Theme.CommandButton("继续", Theme.Blue, 78);
        _stopButton = Theme.CommandButton("停止", Theme.Red, 78);
        _startButton.Click += async (_, _) => await SendControlAsync("start");
        _pauseButton.Click += async (_, _) => await SendControlAsync("pause");
        _resumeButton.Click += async (_, _) => await SendControlAsync("resume");
        _stopButton.Click += async (_, _) => await SendControlAsync("stop");
        commands.Controls.AddRange(new Control[] { _startButton, _pauseButton, _resumeButton, _stopButton });
        layout.Controls.Add(commands, 0, 1);

        TableLayoutPanel options = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 6)
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        _mode = new ComboBox
        {
            Dock = DockStyle.Bottom,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Height = 29,
            Font = Theme.Body(9f)
        };
        _mode.Items.AddRange(new object[] { "普通模式", "随机模式" });
        _speed = NumberInput(0, 2, 2);
        _maxMinutes = NumberInput(5, 480, 120);
        options.Controls.Add(OptionField("模式", _mode), 0, 0);
        options.Controls.Add(OptionField("速度", _speed), 1, 0);
        options.Controls.Add(OptionField("最长分钟", _maxMinutes), 2, 0);
        layout.Controls.Add(options, 0, 2);

        _stageBanner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(230, 242, 241),
            Padding = new Padding(14, 9, 14, 8),
            Margin = new Padding(0, 0, 0, 8)
        };
        _runState = new Label
        {
            Text = "待机 / 等待游戏",
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Theme.TealDark,
            Font = Theme.Body(11f, FontStyle.Bold),
            AutoEllipsis = true
        };
        _stageDetail = new Label
        {
            Text = "启动并完成安全握手后可开始自动游玩",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.5f),
            AutoEllipsis = true
        };
        _stageBanner.Controls.Add(_stageDetail);
        _stageBanner.Controls.Add(_runState);
        layout.Controls.Add(_stageBanner, 0, 3);

        _timeline = new TimelineControl { Dock = DockStyle.Fill };
        layout.Controls.Add(_timeline, 0, 4);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildTelemetryPanel()
    {
        SectionPanel section = new() { Dock = DockStyle.Fill };
        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        shell.Controls.Add(SectionHeading("运行遥测"), 0, 0);

        Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        TableLayoutPanel values = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 0,
            Margin = Padding.Empty
        };
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        AddTelemetry(values, "product", "产品");
        AddTelemetry(values, "gameVersion", "游戏版本");
        AddTelemetry(values, "pluginVersion", "插件版本");
        AddTelemetry(values, "protocol", "协议");
        AddTelemetry(values, "unity", "Unity");
        AddTelemetry(values, "buildGuid", "构建 GUID");
        AddTelemetry(values, "assembly", "程序集 SHA-256");
        AddTelemetry(values, "mvid", "程序集 MVID");
        AddTelemetry(values, "fingerprint", "指纹门禁");
        AddTelemetry(values, "runtime", "运行时合同");
        AddTelemetry(values, "isolation", "存档隔离");
        AddTelemetry(values, "platform", "平台写入");
        AddTelemetry(values, "artifacts", "产物重定向");
        AddTelemetry(values, "profile", "隔离档目录");
        AddTelemetry(values, "evidence", "证据目录");
        AddTelemetry(values, "integrity", "测试完整性");
        AddTelemetry(values, "outcome", "本局结果");
        AddTelemetry(values, "waves", "波次");
        AddTelemetry(values, "process", "进程状态");
        scroll.Controls.Add(values);
        shell.Controls.Add(scroll, 0, 1);

        _openEvidenceButton = Theme.CommandButton("打开证据目录", Theme.Blue, 142);
        _openEvidenceButton.Location = new Point(0, 10);
        _openEvidenceButton.Margin = Padding.Empty;
        _openEvidenceButton.Click += (_, _) => OpenEvidenceDirectory();
        Panel footer = new()
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = Theme.Surface
        };
        Panel footerDivider = new()
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.Line
        };
        footer.Controls.Add(_openEvidenceButton);
        footer.Controls.Add(footerDivider);
        shell.Controls.Add(footer, 0, 2);
        section.Controls.Add(shell);
        return section;
    }

    private Control BuildLogPanel()
    {
        SectionPanel section = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
        Panel header = new() { Dock = DockStyle.Top, Height = 34 };
        Label heading = SectionHeading("运行日志");
        heading.Dock = DockStyle.Left;
        Button clear = Theme.CommandButton("清空", Theme.Muted, 58);
        clear.Dock = DockStyle.Right;
        clear.Height = 27;
        clear.Click += (_, _) => _logs.Clear();
        header.Controls.Add(clear);
        header.Controls.Add(heading);
        _logs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Console,
            ForeColor = Theme.ConsoleText,
            Font = Theme.Data(8.5f),
            DetectUrls = false,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            ShortcutsEnabled = true
        };
        section.Controls.Add(_logs);
        section.Controls.Add(header);
        return section;
    }

    private void BindSettings()
    {
        _settings.NormalizeUpdateSource();
        _gamePath.Text = _settings.GameRoot;
        _profileName.Text = string.IsNullOrWhiteSpace(_settings.ProfileName) ? "qa-default" : _settings.ProfileName;
        _continueProfile.Checked = _settings.ContinueExistingProfile;
        _mode.SelectedIndex = _settings.GameMode == AutomationGameMode.Random ? 1 : 0;
        _speed.Value = Math.Clamp(_settings.SpeedState, 0, 2);
        _maxMinutes.Value = Math.Clamp(_settings.MaxRunMinutes, 5, 480);
        _autoUpdateCheck.Checked = _settings.CheckUpdatesOnStart;
    }

    private async Task OnShownAsync(string settingsWarning)
    {
        if (!string.IsNullOrWhiteSpace(settingsWarning))
        {
            AppendLog("WARN", settingsWarning, Theme.Amber);
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

        AppendLog("INFO", "Manager 已启动；等待选择并验证 Skyspine 测试包。", Theme.ConsoleText);
        if (!string.IsNullOrWhiteSpace(_settings.GameRoot))
        {
            await ValidateGameAsync(_settings.GameRoot);
        }

        if (_settings.CheckUpdatesOnStart && _updates.IsConfigured(_settings))
        {
            await CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private async Task BrowseForGameAsync()
    {
        if (_launchOptions.DemoMode) return;
        using FolderBrowserDialog dialog = new()
        {
            Description = "选择 Loopstructor 2: Skyspine 的 Windows 打包目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = Directory.Exists(_gamePath.Text) ? _gamePath.Text : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await ValidateGameAsync(dialog.SelectedPath);
        }
    }

    private async Task ValidateGameAsync(string root)
    {
        SetBusy(true);
        _gamePath.Text = root;
        _validationState.Text = "正在验证构建...";
        _validationState.ForeColor = Theme.Blue;
        try
        {
            GameInstallValidation validation = await _validator.ValidateAsync(root, _lifetime.Token);
            _game = validation.IsValid ? validation : null;
            if (validation.IsValid)
            {
                _settings.GameRoot = validation.GameRoot;
                _gamePath.Text = validation.GameRoot;
                _validationState.Text = $"已验证 Skyspine {Display(validation.ProductVersion)} / {ShortHash(validation.AssemblySha256)}";
                _validationState.ForeColor = Theme.TealDark;
                AppendLog("SAFE", "已验证所选 Skyspine 包及自动化运行时合同。", Theme.Teal);
                foreach (string warning in validation.Warnings)
                {
                    AppendLog("WARN", warning, Theme.Amber);
                }

                ApplyBuildTelemetry(validation);
                RefreshPluginStatus();
                SaveSettings();
            }
            else
            {
                _validationState.Text = validation.Errors.FirstOrDefault() ?? "构建验证失败";
                _validationState.ForeColor = Theme.Red;
                foreach (string error in validation.Errors)
                {
                    AppendLog("ERROR", error, Theme.Red);
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
        if (_launchOptions.DemoMode) return;
        if (_game == null) return;
        SetBusy(true);
        try
        {
            PluginOperationResult result = await _installer.InstallAsync(_game, _lifetime.Token);
            AppendOperation(result);
            RefreshPluginStatus();
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
        if (_launchOptions.DemoMode) return;
        if (_game == null || _pluginStatus == null) return;
        bool enable = _pluginStatus.State == PluginState.Disabled;
        PluginOperationResult result = _installer.SetEnabled(_game.GameRoot, enable);
        AppendOperation(result);
        RefreshPluginStatus();
    }

    private void UninstallPlugin()
    {
        if (_launchOptions.DemoMode) return;
        if (_game == null) return;
        DialogResult confirmation = MessageBox.Show(
            this,
            "仅删除 AutoPlayer 插件与其配置，保留共享 BepInEx 运行时。继续？",
            "卸载 AutoPlayer 插件",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirmation != DialogResult.OK) return;
        PluginOperationResult result = _installer.Uninstall(_game.GameRoot);
        AppendOperation(result);
        RefreshPluginStatus();
    }

    private void LaunchGame()
    {
        if (_launchOptions.DemoMode) return;
        if (_game == null || _pluginStatus?.State != PluginState.Enabled)
        {
            AppendLog("ERROR", "启动前必须验证测试包并启用插件。", Theme.Red);
            return;
        }

        SaveSettings();
        GameLaunchResult result = _gameLauncher.Launch(_game, _settings.ProfileName);
        if (!result.Success || result.Session == null)
        {
            AppendLog("ERROR", result.Message, Theme.Red);
            return;
        }

        _session = result.Session;
        _hello = null;
        _status = null;
        _sessionTrusted = false;
        _legacyProbeDone = false;
        _transportFailures = 0;
        _lastStatusSignature = string.Empty;
        _lastTrustError = string.Empty;
        _restartWarningReported = false;
        _cheatForm?.UpdateSession(false, null, null);
        _logTail.Reset(_session.LogPath);
        _connection.SetState("等待插件", Theme.Amber);
        _runState.Text = "启动中 / 安全握手";
        _stageDetail.Text = "正在核对进程、程序集指纹、隔离目录与平台写入门禁";
        AppendLog("INFO", result.Message, Theme.Blue);
        AppendLog("SAFE", "只接受所选目录、当前 SHA-256 与本次随机管道对应的插件。", Theme.Teal);
        AppendLog("CHEAT", "安全握手通过后可随时打开作弊工具；作弊写操作会标记本进程并要求重启。", Theme.Amber);
        _pollTimer.Start();
        SetOperationAvailability();
        _ = PollPluginAsync();
    }

    private async Task PollPluginAsync()
    {
        if (_pollInProgress || _session == null || _launchOptions.DemoMode) return;
        _pollInProgress = true;
        try
        {
            ReadPlayerLog();
            PipeCallResult call = _hello == null || !_sessionTrusted
                ? await _pipeClient.HelloAsync(_session, _lifetime.Token)
                : await _pipeClient.StatusAsync(_session, _lifetime.Token);
            if (!call.TransportSuccess)
            {
                _transportFailures++;
                _connection.SetState("未连接", Theme.Amber);
                if (_transportFailures == 4)
                {
                    AppendLog("WARN", "尚未收到所选测试包的插件握手：" + call.Error, Theme.Amber);
                }

                if (_transportFailures >= 6 && !_legacyProbeDone)
                {
                    _legacyProbeDone = true;
                    PipeCallResult legacy = await _pipeClient.ProbeLegacyStatusAsync(_lifetime.Token);
                    if (legacy.TransportSuccess)
                    {
                        AppendLog(
                            "ERROR",
                            "检测到旧版固定管道，仅作诊断，Manager 不会向其发送控制命令。请重装当前插件并重启测试包。",
                            Theme.Red);
                    }
                }

                CheckSelectedProcessBoundary();
                return;
            }

            _transportFailures = 0;
            ControlResponse? response = call.Response;
            if (response == null)
            {
                return;
            }

            if (!response.Success)
            {
                _connection.SetState("插件拒绝", Theme.Red);
                AppendLog("ERROR", response.Message, Theme.Red);
                return;
            }

            if (response.Hello != null && !_sessionTrusted)
            {
                _hello = response.Hello;
                _sessionTrusted = ValidateHello(_hello, out string trustError);
                if (!_sessionTrusted)
                {
                    _connection.SetState("门禁失败", Theme.Red);
                    if (!string.Equals(trustError, _lastTrustError, StringComparison.Ordinal))
                    {
                        _lastTrustError = trustError;
                        AppendLog("ERROR", trustError, Theme.Red);
                    }
                }
                else
                {
                    _lastTrustError = string.Empty;
                    int launchProcessId = _session.ProcessId ?? 0;
                    _session.ProcessId = _hello.GameProcessId;
                    _session.DeleteTicket();
                    _connection.SetState("安全连接", Theme.Teal);
                    AppendLog("SAFE", "插件握手与本次构建指纹一致，控制通道已启用。", Theme.Teal);
                    if (launchProcessId != _hello.GameProcessId)
                    {
                        AppendLog(
                            "SAFE",
                            $"已从启动进程 PID {launchProcessId} 切换为经路径验证的游戏 PID {_hello.GameProcessId}。",
                            Theme.Teal);
                    }
                    AppendLog("SAFE", "一次性备用授权票据已在可信握手后清理。", Theme.Teal);
                }

                ApplyHello(_hello);
                _cheatForm?.UpdateSession(_sessionTrusted, _hello, _status);
            }

            if (response.Status != null)
            {
                ApplyStatus(response.Status);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AppendLog("ERROR", "轮询失败：" + exception.Message, Theme.Red);
        }
        finally
        {
            _pollInProgress = false;
            SetOperationAvailability();
        }
    }

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
                Theme.Amber);
            return;
        }

        if (!_sessionTrusted || _session == null)
        {
            AppendLog("ERROR", "安全握手未通过，控制命令未发送。", Theme.Red);
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
                AppendLog("ERROR", $"{ControlCommandName(command)}发送失败：{result.Error}", Theme.Red);
                return;
            }

            ControlResponse response = result.Response!;
            AppendLog(response.Success ? "ACT" : "ERROR", response.Message, response.Success ? Theme.Teal : Theme.Red);
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
        if (_launchOptions.DemoMode || !_sessionTrusted || _session == null)
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
                bool outcomeUnknown = result.RequestMayHaveExecuted && CheatCommands.IsMutationCommand(command);
                string message = outcomeUnknown
                    ? "作弊写命令已发送，但连续两次未能取回同一请求 ID 的结果。为避免重复执行，本窗口已冻结写操作；请关闭游戏并重新启动测试进程。"
                    : result.Error;
                AppendLog("ERROR", "作弊命令发送失败：" + message, Theme.Red);
                return new ControlResponse
                {
                    Success = false,
                    Message = message,
                    Data = new JObject { ["outcomeUnknown"] = outcomeUnknown }
                };
            }

            ControlResponse response = result.Response!;
            if (!response.Success || !string.Equals(command, CheatCommands.QueryState, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(response.Success ? "CHEAT" : "ERROR", response.Message, response.Success ? Theme.Amber : Theme.Red);
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
            AppendLog("ERROR", "作弊命令执行失败：" + exception.Message, Theme.Red);
            return new ControlResponse { Success = false, Message = exception.Message };
        }
    }

    private void OpenCheatForm()
    {
        if (_cheatForm == null || _cheatForm.IsDisposed)
        {
            _cheatForm = new CheatForm(SendCheatCommandAsync);
            _cheatForm.FormClosed += (_, _) => _cheatForm = null;
        }

        _cheatForm.UpdateSession(_sessionTrusted, _hello, _status);
        if (!_cheatForm.Visible)
        {
            _cheatForm.Show(this);
        }

        _cheatForm.BringToFront();
        _cheatForm.Activate();
    }

    private AutomationRunOptions BuildRunOptions()
    {
        return new AutomationRunOptions
        {
            Mode = _mode.SelectedIndex == 1 ? AutomationGameMode.Random : AutomationGameMode.Common,
            SpeedState = (int)_speed.Value,
            MaxRunMinutes = (int)_maxMinutes.Value,
            ContinueExistingProfile = _continueProfile.Checked
        };
    }

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

        if (!ValidateGameProcess(hello.GameProcessId, _game.ExecutablePath, out error))
        {
            return false;
        }

        if (!string.Equals(hello.AssemblySha256, _game.AssemblySha256, StringComparison.OrdinalIgnoreCase)
            || !hello.ProductIdentityValid
            || !hello.FingerprintAccepted)
        {
            error = "插件报告的产品身份或 Assembly-CSharp SHA-256 与所选测试包不一致。";
            return false;
        }

        if (!hello.RuntimeContractAvailable)
        {
            error = "所选测试包缺少自动化运行时成员：" + string.Join(", ", hello.MissingMembers);
            return false;
        }

        if (!hello.SaveIsolationApplied || !hello.SaveIsolationVerified)
        {
            error = "存档隔离未应用或未通过写入验证，拒绝自动游玩。";
            return false;
        }

        if (!hello.PlatformWritesBlocked || !hello.GameArtifactsRedirected)
        {
            error = "平台写入或游戏产物重定向门禁未生效，拒绝自动游玩。";
            return false;
        }

        if (!SamePath(hello.ProfileRoot, _session.Ticket.ProfileRoot)
            || !SamePath(hello.ArtifactRoot, _session.Ticket.ArtifactRoot))
        {
            error = "插件使用的隔离档或证据目录不属于本次启动会话。";
            return false;
        }

        if (hello.CheatSessionAuthorized != _session.Ticket.CheatModeAllowed)
        {
            error = "插件回报的作弊控制授权与本次 Manager 启动票据不一致。";
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
        SetTelemetry("isolation", hello.SaveIsolationVerified ? "已应用并验证" : "未验证");
        SetTelemetry("platform", hello.PlatformWritesBlocked ? "已阻断" : "未阻断");
        SetTelemetry("artifacts", hello.GameArtifactsRedirected ? "已重定向" : "未重定向");
        SetTelemetry("profile", hello.ProfileRoot);
        SetTelemetry("evidence", hello.ArtifactRoot);
    }

    private void ApplyStatus(AutoPlayerStatus status)
    {
        _status = status;
        _runState.Text = RunStateName(status.RunState) + " / " + TimelineControl.StageName(status.Stage);
        _stageDetail.Text = string.IsNullOrWhiteSpace(status.StageDetail) ? Display(status.LastMessage) : status.StageDetail;
        _timeline.SetEvents(status.Timeline);
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
        SetTelemetry("isolation", status.SaveIsolationVerified ? "已应用并验证" : "未验证");
        SetTelemetry("platform", status.PlatformWritesBlocked ? "已阻断" : "未阻断");
        SetTelemetry("artifacts", status.GameArtifactsRedirected ? "已重定向" : "未重定向");
        SetTelemetry("profile", status.IsolatedSaveRoot);
        SetTelemetry("evidence", string.IsNullOrWhiteSpace(status.EvidenceDirectory) ? status.ArtifactDirectory : status.EvidenceDirectory);
        SetTelemetry(
            "integrity",
            status.CheatUsed
                ? status.CheatActionCount > 0
                    ? $"QA 档已污染 / 本进程 {status.CheatActionCount} 项"
                    : "QA 档已污染 / 历史作弊"
                : "正常测试");
        SetTelemetry("outcome", OutcomeName(status.Outcome));
        SetTelemetry("waves", $"{status.WavesCompleted} 完成 / {status.WavesStarted} 启动");
        int processId = _hello?.GameProcessId ?? _session?.ProcessId ?? 0;
        string processPrefix = processId > 0 ? $"PID {processId} / " : string.Empty;
        SetTelemetry(
            "process",
            processPrefix + (status.CheatUsed
                ? "QA 档只可用于作弊测试"
                : status.NeedsProcessRestart ? "必须彻底重启" : "可继续测试"));

        string signature = $"{status.RunState}|{status.Outcome}|{status.Stage}|{status.LastCommand}|{status.LastMessage}|{status.NeedsProcessRestart}|{status.CheatModeEnabled}|{status.CheatUsed}|{status.CheatActionCount}";
        if (!string.Equals(signature, _lastStatusSignature, StringComparison.Ordinal))
        {
            _lastStatusSignature = signature;
            AppendLog("STATE", $"{RunStateName(status.RunState)} / {TimelineControl.StageName(status.Stage)} / {status.LastMessage}", Theme.Blue);
        }

        bool statusTrusted = status.ProductIdentityValid
                             && status.FingerprintAccepted
                             && status.RuntimeContractAvailable
                             && status.SaveIsolationApplied
                             && status.SaveIsolationVerified
                             && status.PlatformWritesBlocked
                             && status.GameArtifactsRedirected
                             && (_game == null || string.Equals(
                                 status.AssemblySha256,
                                 _game.AssemblySha256,
                                 StringComparison.OrdinalIgnoreCase))
                             && (_session == null
                                 || (SamePath(status.IsolatedSaveRoot, _session.Ticket.ProfileRoot)
                                     && SamePath(status.ArtifactDirectory, _session.Ticket.ArtifactRoot)));
        if (!statusTrusted)
        {
            _sessionTrusted = false;
            _connection.SetState("门禁失败", Theme.Red);
        }
        else if (_sessionTrusted)
        {
            _connection.SetState(StatusBadgeText(status.RunState), ColorForRunState(status.RunState));
        }
        else
        {
            _connection.SetState("等待门禁", Theme.Amber);
        }

        if (status.CheatModeEnabled)
        {
            _stageBanner.BackColor = Color.FromArgb(252, 242, 218);
            _runState.ForeColor = Color.FromArgb(130, 82, 10);
            _runState.Text = "作弊模式 / 已启用";
            _stageDetail.Text = status.CheatUsed
                ? status.CheatActionCount > 0
                    ? $"本进程已尝试 {status.CheatActionCount} 项作弊操作；当前 QA 档已永久标记为污染。"
                    : "当前 QA 档存在历史作弊污染标记，只能继续用于作弊测试。"
                : "作弊工具已就绪；尚未执行会改变对局的操作。";
            _connection.SetState("作弊模式", Theme.Amber);
            SetTelemetry("process", status.CheatUsed ? "QA 档已污染 / 只能作弊测试" : "作弊模式已启用");
            _restartWarningReported = status.CheatUsed;
        }
        else if (status.CheatUsed)
        {
            _stageBanner.BackColor = Color.FromArgb(252, 242, 218);
            _runState.ForeColor = Color.FromArgb(130, 82, 10);
            _runState.Text = "QA 档 / 已被作弊修改";
            _stageDetail.Text = "普通自动游玩已禁用。请改用新的 QA 配置名称建立干净测试档；当前档仍可继续作弊测试。";
            _connection.SetState("QA 档污染", Theme.Amber);
            SetTelemetry("process", "QA 档已污染 / 只能作弊测试");
            if (!_restartWarningReported)
            {
                _restartWarningReported = true;
                AppendLog(
                    "WARN",
                    "当前 QA 档存在持久作弊污染标记，普通自动游玩已禁用。请使用新的 QA 配置名称建立干净测试档。",
                    Theme.Amber);
            }
        }
        else if (status.NeedsProcessRestart)
        {
            ShowRestartRequired();
            if (!_restartWarningReported)
            {
                _restartWarningReported = true;
                AppendLog(
                    "WARN",
                    "插件要求重启游戏进程。请彻底关闭 Skyspine，再由 Manager 重新启动；当前进程禁止开始新一轮自动游玩。",
                    Theme.Amber);
            }
        }
        else
        {
            _restartWarningReported = false;
            _stageBanner.BackColor = Color.FromArgb(230, 242, 241);
            _runState.ForeColor = Theme.TealDark;
        }

        _cheatForm?.UpdateSession(_sessionTrusted, _hello, status);
        SetOperationAvailability();
    }

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
        _pluginState.ForeColor = _pluginStatus.State switch
        {
            PluginState.Enabled => Theme.TealDark,
            PluginState.Disabled => Theme.Amber,
            PluginState.Incomplete => Theme.Red,
            _ => Theme.Muted
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
        _installButton.Enabled = validGame;
        _togglePluginButton.Enabled = validGame && _pluginStatus?.State is PluginState.Enabled or PluginState.Disabled;
        _togglePluginButton.Text = _pluginStatus?.State == PluginState.Disabled ? "启用" : "停用";
        _uninstallButton.Enabled = validGame && _pluginStatus?.State != PluginState.NotInstalled;
        _launchButton.Enabled = validGame && _pluginStatus?.State == PluginState.Enabled;
        _continueProfile.Enabled = validGame;
        _cheatButton.Enabled = _sessionTrusted
                               && (_status?.CheatSessionAuthorized == true
                                   || _hello?.CheatSessionAuthorized == true
                                   || _status?.CheatAvailable == true
                                   || _hello?.CheatAvailable == true);
        _openEvidenceButton.Enabled = !string.IsNullOrWhiteSpace(EvidenceDirectory());
        SetControlButtons(_sessionTrusted);
    }

    private void SetControlButtons(bool enabled)
    {
        RunControlAvailability availability = RunControlAvailability.From(enabled, _status);
        _startButton.Enabled = availability.CanStart;
        _pauseButton.Enabled = availability.CanPause;
        _resumeButton.Enabled = availability.CanResume;
        _stopButton.Enabled = availability.CanStop;
    }

    private void ShowRestartRequired()
    {
        _stageBanner.BackColor = Color.FromArgb(252, 237, 225);
        _runState.ForeColor = Theme.Red;
        _runState.Text = "必须重启 / 当前进程不可继续";
        _stageDetail.Text = "彻底关闭 Skyspine 游戏进程后，再由 Manager 重新启动；当前进程不能开始新一轮自动游玩。";
        _connection.SetState("需要重启", Theme.Red);
        SetTelemetry("process", "必须彻底重启");
        SetControlButtons(_sessionTrusted);
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _browseButton.Enabled = !busy;
        _continueProfile.Enabled = !busy;
        if (busy)
        {
            _installButton.Enabled = false;
            _togglePluginButton.Enabled = false;
            _uninstallButton.Enabled = false;
            _launchButton.Enabled = false;
        }
    }

    private void SetInteractiveControls(bool enabled)
    {
        foreach (Control control in new Control[]
                 {
                     _browseButton, _installButton, _togglePluginButton, _uninstallButton, _launchButton,
                     _cheatButton, _startButton, _pauseButton, _resumeButton, _stopButton, _updateButton
                 })
        {
            control.Enabled = enabled;
        }
    }

    private void CheckSelectedProcessBoundary()
    {
        if (_session?.ProcessId is not > 0 || _transportFailures != 5)
        {
            return;
        }

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
            "所选测试包已退出。若 Steam RestartAppIfNecessary 启动了另一安装目录，Manager 不会跟随或控制该进程；请从正确目录重新启动测试包。",
            Theme.Red);
    }

    private void ReadPlayerLog()
    {
        try
        {
            foreach (string line in _logTail.ReadAvailable())
            {
                Color color = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                              || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                              || line.Contains("错误", StringComparison.Ordinal)
                              || line.Contains("异常", StringComparison.Ordinal)
                    ? Theme.Red
                    : line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("警告", StringComparison.Ordinal)
                        ? Theme.Amber
                        : Theme.ConsoleText;
                AppendLog("GAME", line, color);
            }
        }
        catch (Exception exception)
        {
            AppendLog("WARN", "Player.log 暂时无法读取：" + exception.Message, Theme.Amber);
        }
    }

    private async Task UpdateButtonOnClickAsync()
    {
        if (_launchOptions.DemoMode) return;
        SaveSettings();
        if (_updateAvailable)
        {
            DialogResult confirmation = MessageBox.Show(
                this,
                "Updater 将等待游戏与 Manager 退出，再校验并替换工具文件。现在开始？",
                "安装 AutoPlayer 更新",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (confirmation != DialogResult.OK) return;
            (bool success, string message) = _updates.StartApply(_settings, _session?.ProcessId);
            AppendLog(success ? "INFO" : "ERROR", message, success ? Theme.Blue : Theme.Red);
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
        _updateButton.Enabled = false;
        _updateState.Text = "正在检查...";
        ManagerUpdateStatus result = await _updates.CheckAsync(_settings, _lifetime.Token);
        _updateButton.Enabled = true;
        _updateAvailable = result.Success && result.UpdateAvailable;
        _updateState.Text = result.UpdateAvailable
            ? $"可更新 {result.LatestVersion}"
            : result.Success ? "当前已是最新版" : "更新检查不可用";
        _updateButton.Text = result.UpdateAvailable ? "安装更新" : "检查更新";
        if (userInitiated || result.UpdateAvailable)
        {
            AppendLog(result.Success ? "INFO" : "WARN", result.Message, result.Success ? Theme.Blue : Theme.Amber);
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
        _validationState.ForeColor = Theme.TealDark;
        _pluginState.Text = "插件已启用  " + _hello.PluginVersion;
        _pluginState.ForeColor = Theme.TealDark;
        _sessionTrusted = true;
        _connection.SetState("安全连接", Theme.Teal);
        ApplyBuildTelemetry(_game);
        ApplyHello(_hello);
        ApplyStatus(_status);
        _updateState.Text = "演示数据";
        foreach (string line in DemoData.LogLines())
        {
            AppendLog(string.Empty, line, line.Contains("安全", StringComparison.Ordinal) ? Theme.Teal : Theme.ConsoleText);
        }

        _openEvidenceButton.Enabled = false;
    }

    private async Task CaptureScreenshotAsync()
    {
        await Task.Delay(450);
        string output = string.IsNullOrWhiteSpace(_launchOptions.ScreenshotOutput)
            ? Path.Combine(Protocol.DataRoot, "artifacts", "manager-screenshot.png")
            : _launchOptions.ScreenshotOutput;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Control captureTarget = _launchOptions.DemoCheatWindow && _cheatForm is { IsDisposed: false }
            ? _cheatForm
            : _captureSurface;
        Size captureSize = _launchOptions.DemoCheatWindow
            ? captureTarget.Size
            : _launchOptions.WindowSize ?? captureTarget.ClientSize;
        using Bitmap bitmap = new(captureSize.Width, captureSize.Height);
        captureTarget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, captureSize));
        bitmap.Save(output, ImageFormat.Png);
        if (_launchOptions.ExitAfterScreenshot)
        {
            BeginInvoke(Close);
        }
    }

    private void OpenEvidenceDirectory()
    {
        string directory = EvidenceDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            AppendLog("WARN", "证据目录尚未创建。", Theme.Amber);
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
            AppendLog("ERROR", "无法打开证据目录：" + exception.Message, Theme.Red);
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
        AppendLog(result.Success ? "INFO" : "ERROR", result.Message, result.Success ? Theme.Teal : Theme.Red);
    }

    private void AppendLog(string category, string message, Color color)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(message)) return;
        string prefix = string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : DateTime.Now.ToString("HH:mm:ss.fff") + "  " + LogCategoryName(category).PadRight(4) + " ";
        _logs.SelectionStart = _logs.TextLength;
        _logs.SelectionLength = 0;
        _logs.SelectionColor = color;
        _logs.AppendText(prefix + message.Replace("\r", string.Empty).Replace("\n", " ") + Environment.NewLine);
        _logs.SelectionColor = _logs.ForeColor;
        if (_logs.Lines.Length > 1500)
        {
            string[] keep = _logs.Lines.TakeLast(1000).ToArray();
            _logs.Lines = keep;
        }

        _logs.SelectionStart = _logs.TextLength;
        _logs.ScrollToCaret();
    }

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
        _settings.ProfileName = string.IsNullOrWhiteSpace(_profileName.Text) ? "qa-default" : _profileName.Text.Trim();
        _settings.ContinueExistingProfile = _continueProfile.Checked;
        _settings.GameMode = _mode.SelectedIndex == 1 ? AutomationGameMode.Random : AutomationGameMode.Common;
        _settings.SpeedState = (int)_speed.Value;
        _settings.MaxRunMinutes = (int)_maxMinutes.Value;
        _settings.NormalizeUpdateSource();
        _settings.CheckUpdatesOnStart = _autoUpdateCheck.Checked;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            AppendLog("WARN", "Manager 设置无法保存：" + exception.Message, Theme.Amber);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
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

    private void AddTelemetry(TableLayoutPanel table, string key, string caption)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        Label name = new()
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.3f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 7, 0)
        };
        Label value = Theme.Value();
        value.Padding = new Padding(0, 0, 4, 0);
        table.Controls.Add(name, 0, row);
        table.Controls.Add(value, 1, row);
        _telemetry[key] = value;
    }

    private void SetTelemetry(string key, string value)
    {
        if (!_telemetry.TryGetValue(key, out Label? label)) return;
        label.Text = Display(value);
        _toolTip.SetToolTip(label, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private static Label SectionHeading(string text) => new()
    {
        Text = text,
        Height = 34,
        AutoSize = false,
        ForeColor = Theme.Ink,
        Font = Theme.Body(11f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static Panel FieldPanel(string caption, int height)
    {
        Panel panel = new() { Width = 250, Height = height, Margin = new Padding(0, 0, 0, 5) };
        Label label = Theme.Caption(caption);
        label.Location = Point.Empty;
        panel.Controls.Add(label);
        return panel;
    }

    private static Panel FieldPanel(string caption, Control input)
    {
        Panel panel = FieldPanel(caption, 55);
        input.Location = new Point(0, 22);
        input.Width = 250;
        panel.Controls.Add(input);
        return panel;
    }

    private static Panel Divider() => new()
    {
        Width = 250,
        Height = 1,
        BackColor = Theme.Line,
        Margin = new Padding(0, 2, 0, 2)
    };

    private static TextBox InputBox() => new()
    {
        Height = 30,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        ForeColor = Theme.Ink,
        Font = Theme.Body(9f)
    };

    private static NumericUpDown NumberInput(decimal minimum, decimal maximum, decimal value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Dock = DockStyle.Bottom,
        Height = 29,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Data(9f)
    };

    private static Control OptionField(string caption, Control input)
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 0, 10, 0),
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label label = Theme.Caption(caption);
        label.Dock = DockStyle.Fill;
        label.Margin = Padding.Empty;
        input.Dock = DockStyle.Top;
        input.Margin = Padding.Empty;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(input, 0, 1);
        return panel;
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
            error = "插件握手未返回有效的游戏进程 PID；请重装当前插件并重新启动测试包。";
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
                error = $"插件进程 PID {processId} 不属于当前选择的测试包，拒绝建立控制通道。";
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

    private static Color ColorForRunState(AutoPlayerRunState state) => state switch
    {
        AutoPlayerRunState.Running => Theme.Teal,
        AutoPlayerRunState.Paused => Theme.Amber,
        AutoPlayerRunState.Completed => Theme.Blue,
        AutoPlayerRunState.Faulted or AutoPlayerRunState.Incompatible => Theme.Red,
        _ => Theme.Teal
    };
}
