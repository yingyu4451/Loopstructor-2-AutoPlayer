using System.Globalization;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed class CheatForm : Form
{
    private const decimal DefaultAttributeMinimum = -1_000_000_000m;
    private const decimal DefaultAttributeMaximum = 1_000_000_000m;

    private readonly Func<string, JObject?, Task<ControlResponse?>> _sendCommand;
    private readonly List<Control> _mutationControls = new();
    private readonly List<Control> _catalogQueryControls = new();
    private readonly List<Control> _entityQueryControls = new();
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, Image> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeCatalogIconKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _capturePollTimer = new() { Interval = 400 };

    private bool _trusted;
    private bool _busy;
    private bool _synchronizing;
    private bool _loadingEntities;
    private bool _writeOutcomeUnknown;
    private BridgeHello? _hello;
    private AutoPlayerStatus? _status;
    private string _sessionKey = string.Empty;
    private string _lastMessage = string.Empty;
    private bool _lastMessageIsError;
    private bool _capturePollInProgress;
    private long _captureEpoch;
    private string _spawnCaptureState = "idle";
    private int _maxEnchantmentsPerVehicle = 5;

    private CheckBox _enableCheck = null!;
    private Label _versionLabel = null!;
    private Panel _statusBanner = null!;
    private Label _statusTitle = null!;
    private Label _statusDetail = null!;
    private TabControl _tabs = null!;

    private Button _catalogRefreshButton = null!;
    private Label _catalogSummary = null!;
    private CatalogPickerControl _vehicleCatalog = null!;
    private NumericUpDown _vehicleCount = null!;
    private CatalogPickerControl _enchantmentCatalog = null!;
    private NumericUpDown _enchantmentLevel = null!;
    private Button _addEnchantmentButton = null!;
    private Button _removeEnchantmentButton = null!;
    private Button _clearEnchantmentsButton = null!;
    private DataGridView _enchantmentGrid = null!;
    private Label _enchantmentSummary = null!;
    private Button _grantVehicleButton = null!;
    private CatalogPickerControl _disposableCatalog = null!;
    private NumericUpDown _disposableCount = null!;
    private Button _grantDisposableButton = null!;
    private CatalogPickerControl _relicCatalog = null!;
    private Button _grantRelicButton = null!;
    private CatalogPickerControl _catapultCatalog = null!;
    private NumericUpDown _catapultCount = null!;
    private Button _grantCatapultButton = null!;

    private CheckBox _baseGodModeCheck = null!;
    private CheckBox _enemyIdOverlayCheck = null!;
    private Button _endWaveButton = null!;
    private Button _clearEnemiesButton = null!;

    private DataGridView _vehicleGrid = null!;
    private DataGridView _enemyGrid = null!;
    private Button _vehicleRefreshButton = null!;
    private Button _enemyRefreshButton = null!;
    private Label _vehicleSummary = null!;
    private Label _enemySummary = null!;
    private ComboBox _vehicleAttribute = null!;
    private ComboBox _enemyAttribute = null!;
    private Label _vehicleCurrentValue = null!;
    private Label _enemyCurrentValue = null!;
    private NumericUpDown _vehicleAttributeValue = null!;
    private NumericUpDown _enemyAttributeValue = null!;
    private Button _modifyVehicleButton = null!;
    private Button _modifyEnemyButton = null!;

    private CatalogPickerControl _enemyCatalog = null!;
    private NumericUpDown _enemyLevel = null!;
    private NumericUpDown _enemyCount = null!;
    private NumericUpDown _spawnX = null!;
    private NumericUpDown _spawnY = null!;
    private NumericUpDown _spawnZ = null!;
    private Button _capturePointButton = null!;
    private Button _cancelCaptureButton = null!;
    private Label _captureStatusLabel = null!;
    private Label _spawnStatusLabel = null!;
    private Button _spawnEnemyButton = null!;

    public CheatForm(Func<string, JObject?, Task<ControlResponse?>> sendCommand)
    {
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _capturePollTimer.Tick += CapturePollTimerTick;

        InitializeWindow();
        BuildInterface();
        ApplyAvailability();
    }

    public void UpdateSession(bool trusted, BridgeHello? hello, AutoPlayerStatus? status)
    {
        if (IsDisposed) return;
        if (InvokeRequired && IsHandleCreated)
        {
            BeginInvoke(new Action(() => UpdateSession(trusted, hello, status)));
            return;
        }

        string nextSessionKey = hello == null
            ? string.Empty
            : $"{hello.GameProcessId}|{hello.BuildGuid}|{hello.AssemblySha256}|{hello.ArtifactRoot}";
        if (!string.Equals(_sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            _sessionKey = nextSessionKey;
            ClearSessionData();
        }

        _trusted = trusted;
        _hello = hello;
        _status = status;

        _synchronizing = true;
        try
        {
            _enableCheck.Checked = status?.CheatModeEnabled ?? hello?.CheatModeEnabled ?? false;
            _enemyIdOverlayCheck.Checked = status?.EnemyIdsVisible ?? false;
            _baseGodModeCheck.Checked = status?.BaseGodModeEnabled ?? false;
        }
        finally
        {
            _synchronizing = false;
        }

        bool cheatModeEnabled = status?.CheatModeEnabled ?? hello?.CheatModeEnabled ?? false;
        if ((!trusted || status?.CheatAvailable != true || !cheatModeEnabled)
            && string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase))
        {
            ResetSpawnPointCapture("选点状态：作弊模式未启用");
        }

        string pluginVersion = status?.PluginVersion ?? hello?.PluginVersion ?? string.Empty;
        _versionLabel.Text = $"{ManagerProductInfo.DisplayText}   /   插件 v{Display(pluginVersion)}   /   作弊协议 v{hello?.CheatProtocolVersion ?? Protocol.CheatCurrentVersion}";
        ApplyAvailability();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_spawnCaptureState, "arming", StringComparison.OrdinalIgnoreCase))
        {
            _captureEpoch++;
            _capturePollTimer.Stop();
            _spawnCaptureState = "idle";
            _ = CancelSpawnPointCaptureOnCloseAsync();
        }

        base.OnFormClosing(e);
    }

    internal void SelectDemoTab(int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _capturePollTimer.Stop();
            _capturePollTimer.Tick -= CapturePollTimerTick;
            _capturePollTimer.Dispose();
            _toolTip.Dispose();
            DisposeCatalogIcons();
        }

        base.Dispose(disposing);
    }

    private void InitializeWindow()
    {
        Text = $"Loopstructor 2.AutoPlayer 作弊工具 - v{ManagerProductInfo.Version}";
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Ink;
        Font = Theme.Body(9f);
        ShowIcon = false;
    }

    private void BuildInterface()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatusBanner(), 0, 1);
        root.Controls.Add(BuildTabs(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        Panel header = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Ink,
            Padding = new Padding(22, 10, 20, 8),
            Margin = Padding.Empty
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Panel identity = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        Label title = new()
        {
            Text = "SKYSPINE  /  作弊工具",
            AutoSize = true,
            Location = new Point(0, 0),
            ForeColor = Color.White,
            Font = Theme.Display(15.5f, FontStyle.Bold)
        };
        _versionLabel = new Label
        {
            Text = $"{ManagerProductInfo.DisplayText}   /   作弊协议 v{Protocol.CheatCurrentVersion}",
            AutoSize = false,
            Location = new Point(2, 31),
            Size = new Size(640, 20),
            ForeColor = Color.FromArgb(187, 199, 205),
            Font = Theme.Data(8.5f),
            AutoEllipsis = true
        };
        identity.Controls.Add(title);
        identity.Controls.Add(_versionLabel);

        _enableCheck = new CheckBox
        {
            Text = "开启作弊模式",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            CheckAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            Font = Theme.Body(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _enableCheck.CheckedChanged += async (_, _) => await EnableCheckChangedAsync();
        _toolTip.SetToolTip(_enableCheck, "仅可信、隔离且没有正在运行或暂停的自动游玩会话可以启用作弊模式。");

        TableLayoutPanel activation = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(10, 0, 0, 0)
        };
        activation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activation.Controls.Add(_enableCheck, 0, 0);

        layout.Controls.Add(identity, 0, 0);
        layout.Controls.Add(activation, 1, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Control BuildStatusBanner()
    {
        _statusBanner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(246, 239, 219),
            Padding = new Padding(22, 8, 22, 6),
            Margin = Padding.Empty
        };
        _statusTitle = new Label
        {
            Text = "等待可信测试会话",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.Amber,
            Font = Theme.Body(9.5f, FontStyle.Bold),
            AutoEllipsis = true
        };
        _statusDetail = new Label
        {
            Text = "启动已安装插件的测试包后，此处会显示作弊功能状态。",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.5f),
            AutoEllipsis = true
        };
        Panel stateRail = new()
        {
            Name = "StateRail",
            Dock = DockStyle.Left,
            Width = 4,
            BackColor = Theme.Amber,
            Margin = Padding.Empty
        };
        _statusBanner.Controls.Add(_statusDetail);
        _statusBanner.Controls.Add(_statusTitle);
        _statusBanner.Controls.Add(stateRail);
        return _statusBanner;
    }

    private Control BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.Body(9.5f, FontStyle.Bold),
            Padding = new Point(20, 7),
            Margin = new Padding(16, 10, 16, 16)
        };
        _tabs.TabPages.Add(BuildResourcesPage());
        _tabs.TabPages.Add(BuildBattlePage());
        _tabs.TabPages.Add(BuildObjectsPage());
        _tabs.TabPages.Add(BuildSpawnPage());
        return _tabs;
    }

    private TabPage BuildResourcesPage()
    {
        TabPage page = CreateTabPage("资源");
        Panel viewport = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Surface,
            Margin = Padding.Empty
        };
        TableLayoutPanel layout = PageLayout(4);
        layout.Dock = DockStyle.Top;
        layout.Height = 540;
        layout.MinimumSize = new Size(880, 540);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 278));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 215));

        FlowLayoutPanel toolbar = HorizontalFlow();
        _catalogRefreshButton = QuietButton("刷新资源目录", 120);
        _catalogRefreshButton.Click += async (_, _) => await RefreshCatalogAsync();
        _catalogSummary = InlineStatus("尚未读取资源目录", 520);
        toolbar.Controls.Add(_catalogRefreshButton);
        toolbar.Controls.Add(_catalogSummary);
        layout.Controls.Add(toolbar, 0, 0);

        _vehicleCatalog = CatalogPicker(270);
        _vehicleCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _vehicleCount = IntegerInput(1, 20, 1, 82);
        _grantVehicleButton = Theme.CommandButton("获取战车", Theme.Teal, 108);
        _grantVehicleButton.Margin = new Padding(0, 23, 8, 0);
        _grantVehicleButton.Click += async (_, _) => await GrantVehicleAsync();

        TableLayoutPanel vehicleGrant = PageLayout(3);
        vehicleGrant.Padding = new Padding(0, 4, 18, 0);
        vehicleGrant.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        vehicleGrant.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        vehicleGrant.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        vehicleGrant.Controls.Add(CatalogField("战车（中文名 / ID）", _vehicleCatalog, 280), 0, 0);
        FlowLayoutPanel vehicleAction = HorizontalFlow(true);
        vehicleAction.Controls.AddRange(new Control[]
        {
            Field("数量", _vehicleCount, 92),
            _grantVehicleButton
        });
        vehicleGrant.Controls.Add(vehicleAction, 0, 1);
        Label vehicleHint = InlineStatus("附魔列表留空时获取普通战车；列表中的附魔会一起应用。", 320);
        vehicleHint.Dock = DockStyle.Fill;
        vehicleGrant.Controls.Add(vehicleHint, 0, 2);

        _enchantmentCatalog = CatalogPicker(270);
        _enchantmentCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _enchantmentLevel = IntegerInput(1, 7, 1, 82);
        _addEnchantmentButton = Theme.CommandButton("添加 / 更新", Theme.Blue, 108);
        _addEnchantmentButton.Margin = new Padding(0, 23, 8, 0);
        _addEnchantmentButton.Click += (_, _) => AddOrUpdateEnchantment();
        _removeEnchantmentButton = QuietButton("移除选中", 92);
        _clearEnchantmentsButton = QuietButton("清空", 64);
        _removeEnchantmentButton.Click += (_, _) => RemoveSelectedEnchantment();
        _clearEnchantmentsButton.Click += (_, _) => ClearEnchantments();
        _enchantmentGrid = CreateEnchantmentGrid();
        _enchantmentGrid.SelectionChanged += (_, _) => ApplyAvailability();
        _enchantmentSummary = InlineStatus("已选 0 / 5", 150);

        TableLayoutPanel enchantmentEditor = PageLayout(3);
        enchantmentEditor.Padding = new Padding(18, 4, 0, 0);
        enchantmentEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        enchantmentEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        enchantmentEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        FlowLayoutPanel enchantmentInputs = HorizontalFlow(true);
        enchantmentInputs.Controls.AddRange(new Control[]
        {
            CatalogField("附魔（可添加多个）", _enchantmentCatalog, 280),
            Field("附魔等级", _enchantmentLevel, 96),
            _addEnchantmentButton
        });
        enchantmentEditor.Controls.Add(enchantmentInputs, 0, 0);
        enchantmentEditor.Controls.Add(_enchantmentGrid, 0, 1);
        FlowLayoutPanel enchantmentActions = HorizontalFlow();
        enchantmentActions.Padding = new Padding(0, 4, 0, 0);
        enchantmentActions.Controls.AddRange(new Control[]
        {
            _removeEnchantmentButton,
            _clearEnchantmentsButton,
            _enchantmentSummary
        });
        enchantmentEditor.Controls.Add(enchantmentActions, 0, 2);

        TableLayoutPanel vehicleBody = PageLayout(1);
        vehicleBody.ColumnCount = 3;
        vehicleBody.RowCount = 1;
        vehicleBody.ColumnStyles.Clear();
        vehicleBody.RowStyles.Clear();
        vehicleBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        vehicleBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        vehicleBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        vehicleBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        vehicleBody.Controls.Add(vehicleGrant, 0, 0);
        vehicleBody.Controls.Add(Divider(), 1, 0);
        vehicleBody.Controls.Add(enchantmentEditor, 2, 0);
        layout.Controls.Add(FlatSection("获取指定战车", vehicleBody, drawBottomLine: false), 0, 1);
        layout.Controls.Add(Divider(), 0, 2);

        _disposableCatalog = CatalogPicker(250);
        _disposableCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _disposableCount = IntegerInput(1, 20, 1, 82);
        _grantDisposableButton = Theme.CommandButton("获取消耗品", Theme.Teal, 116);
        _grantDisposableButton.Margin = new Padding(0, 23, 8, 0);
        _grantDisposableButton.Click += async (_, _) => await GrantDisposableAsync();
        FlowLayoutPanel disposableControls = HorizontalFlow(true);
        disposableControls.Controls.AddRange(new Control[]
        {
            CatalogField("消耗品", _disposableCatalog, 260),
            Field("数量", _disposableCount, 92),
            _grantDisposableButton
        });

        _relicCatalog = CatalogPicker(250);
        _relicCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _grantRelicButton = Theme.CommandButton("获取遗物", Theme.Teal, 108);
        _grantRelicButton.Margin = new Padding(0, 23, 8, 0);
        _grantRelicButton.Click += async (_, _) => await GrantRelicAsync();
        FlowLayoutPanel relicControls = HorizontalFlow(true);
        relicControls.Controls.AddRange(new Control[]
        {
            CatalogField("遗物", _relicCatalog, 260),
            _grantRelicButton
        });

        _catapultCatalog = CatalogPicker(250);
        _catapultCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _catapultCount = IntegerInput(1, 20, 1, 82);
        _grantCatapultButton = Theme.CommandButton("获取弹射点", Theme.Amber, 116);
        _grantCatapultButton.Margin = new Padding(0, 23, 8, 0);
        _grantCatapultButton.Click += async (_, _) => await GrantCatapultPointAsync();
        FlowLayoutPanel catapultControls = HorizontalFlow(true);
        catapultControls.Controls.AddRange(new Control[]
        {
            CatalogField("弹射点", _catapultCatalog, 260),
            Field("数量", _catapultCount, 92),
            _grantCatapultButton
        });

        TableLayoutPanel resourceColumns = PageLayout(1);
        resourceColumns.ColumnCount = 5;
        resourceColumns.RowCount = 1;
        resourceColumns.ColumnStyles.Clear();
        resourceColumns.RowStyles.Clear();
        resourceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        resourceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        resourceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        resourceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        resourceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        resourceColumns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        resourceColumns.Controls.Add(FlatSection("获取指定消耗品", disposableControls, drawBottomLine: false), 0, 0);
        resourceColumns.Controls.Add(Divider(), 1, 0);
        resourceColumns.Controls.Add(FlatSection("获取指定遗物", relicControls, drawBottomLine: false), 2, 0);
        resourceColumns.Controls.Add(Divider(), 3, 0);
        resourceColumns.Controls.Add(FlatSection("获取弹射点", catapultControls, drawBottomLine: false), 4, 0);
        layout.Controls.Add(resourceColumns, 0, 3);

        AddMutationControls(
            _vehicleCatalog, _vehicleCount, _enchantmentCatalog, _enchantmentLevel,
            _addEnchantmentButton, _removeEnchantmentButton, _clearEnchantmentsButton,
            _grantVehicleButton, _disposableCatalog, _disposableCount, _grantDisposableButton,
            _relicCatalog, _grantRelicButton, _catapultCatalog, _catapultCount, _grantCatapultButton);
        _catalogQueryControls.Add(_catalogRefreshButton);
        viewport.Controls.Add(layout);
        viewport.ClientSizeChanged += (_, _) =>
        {
            int availableWidth = Math.Max(0, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            layout.Width = Math.Max(layout.MinimumSize.Width, availableWidth);
        };
        page.Controls.Add(viewport);
        return page;
    }

    private TabPage BuildBattlePage()
    {
        TabPage page = CreateTabPage("战斗");
        TableLayoutPanel layout = PageLayout(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));

        _baseGodModeCheck = Toggle("基地无敌", 150);
        _baseGodModeCheck.Font = Theme.Body(10f, FontStyle.Bold);
        _baseGodModeCheck.CheckedChanged += async (_, _) => await BaseGodModeChangedAsync();
        FlowLayoutPanel baseControls = HorizontalFlow(true);
        baseControls.Controls.Add(Field("基地防护", _baseGodModeCheck, 180));
        layout.Controls.Add(FlatSection("基地状态", baseControls), 0, 0);

        _endWaveButton = Theme.CommandButton("结束当前波次", Theme.Red, 142);
        _clearEnemiesButton = Theme.CommandButton("清除所有敌人", Theme.Red, 142);
        _endWaveButton.Margin = new Padding(0, 23, 12, 0);
        _clearEnemiesButton.Margin = new Padding(0, 23, 12, 0);
        _endWaveButton.Click += async (_, _) => await ConfirmAndExecuteAsync(
            "结束当前波次",
            "确定要立即结束当前波次吗？此操作会改变本轮测试结果。",
            CheatCommands.EndWave);
        _clearEnemiesButton.Click += async (_, _) => await ConfirmAndExecuteAsync(
            "清除所有敌人",
            "确定要清除当前场景中的所有敌人吗？此操作无法撤销。",
            CheatCommands.ClearEnemies);
        FlowLayoutPanel waveControls = HorizontalFlow(true);
        waveControls.Controls.AddRange(new Control[] { _endWaveButton, _clearEnemiesButton });
        layout.Controls.Add(FlatSection("波次操作", waveControls), 0, 1);

        _enemyIdOverlayCheck = Toggle("在游戏中显示敌人 ID", 240);
        _enemyIdOverlayCheck.Font = Theme.Body(10f, FontStyle.Bold);
        _enemyIdOverlayCheck.CheckedChanged += async (_, _) => await EnemyIdOverlayChangedAsync();
        FlowLayoutPanel overlayControls = HorizontalFlow(true);
        overlayControls.Controls.Add(Field("敌人标识", _enemyIdOverlayCheck, 270));
        layout.Controls.Add(FlatSection("敌人 ID 叠字", overlayControls, drawBottomLine: false), 0, 2);

        AddMutationControls(_baseGodModeCheck, _endWaveButton, _clearEnemiesButton, _enemyIdOverlayCheck);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildObjectsPage()
    {
        TabPage page = CreateTabPage("对象属性");
        TableLayoutPanel layout = PageLayout(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        layout.Controls.Add(BuildVehicleEditor(), 0, 0);
        layout.Controls.Add(Divider(), 0, 1);
        layout.Controls.Add(BuildEnemyEditor(), 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildVehicleEditor()
    {
        TableLayoutPanel layout = EntityLayout();
        layout.Controls.Add(SectionHeading("当前战车"), 0, 0);
        FlowLayoutPanel toolbar = HorizontalFlow();
        _vehicleRefreshButton = QuietButton("刷新战车", 92);
        _vehicleRefreshButton.Click += async (_, _) => await RefreshVehiclesAsync();
        _vehicleSummary = InlineStatus("尚未读取战车", 420);
        toolbar.Controls.Add(_vehicleRefreshButton);
        toolbar.Controls.Add(_vehicleSummary);
        layout.Controls.Add(toolbar, 0, 1);

        _vehicleGrid = CreateVehicleGrid();
        _vehicleGrid.SelectionChanged += (_, _) => EntitySelectionChanged(
            _vehicleGrid,
            _vehicleAttribute,
            _vehicleCurrentValue,
            _vehicleAttributeValue);
        layout.Controls.Add(_vehicleGrid, 0, 2);

        _vehicleAttribute = AttributeCombo(190);
        _vehicleCurrentValue = InlineStatus("当前值 -", 205);
        _vehicleAttributeValue = AttributeInput(150);
        _modifyVehicleButton = Theme.CommandButton("应用绝对值", Theme.Blue, 112);
        _modifyVehicleButton.Margin = new Padding(0, 23, 8, 0);
        _vehicleAttribute.SelectedIndexChanged += (_, _) => AttributeSelectionChanged(
            _vehicleAttribute,
            _vehicleCurrentValue,
            _vehicleAttributeValue);
        _modifyVehicleButton.Click += async (_, _) => await ModifyVehicleAsync();
        FlowLayoutPanel editor = HorizontalFlow(true);
        editor.Controls.AddRange(new Control[]
        {
            Field("属性", _vehicleAttribute, 200),
            Field("当前 / 基础", _vehicleCurrentValue, 215),
            Field("新绝对值", _vehicleAttributeValue, 160),
            _modifyVehicleButton
        });
        layout.Controls.Add(editor, 0, 3);

        _entityQueryControls.Add(_vehicleRefreshButton);
        AddMutationControls(_vehicleAttribute, _vehicleAttributeValue, _modifyVehicleButton);
        return layout;
    }

    private Control BuildEnemyEditor()
    {
        TableLayoutPanel layout = EntityLayout();
        layout.Controls.Add(SectionHeading("当前敌人"), 0, 0);
        FlowLayoutPanel toolbar = HorizontalFlow();
        _enemyRefreshButton = QuietButton("刷新敌人", 92);
        _enemyRefreshButton.Click += async (_, _) => await RefreshEnemiesAsync();
        _enemySummary = InlineStatus("尚未读取敌人", 420);
        toolbar.Controls.Add(_enemyRefreshButton);
        toolbar.Controls.Add(_enemySummary);
        layout.Controls.Add(toolbar, 0, 1);

        _enemyGrid = CreateEnemyGrid();
        _enemyGrid.SelectionChanged += (_, _) => EntitySelectionChanged(
            _enemyGrid,
            _enemyAttribute,
            _enemyCurrentValue,
            _enemyAttributeValue);
        layout.Controls.Add(_enemyGrid, 0, 2);

        _enemyAttribute = AttributeCombo(190);
        _enemyCurrentValue = InlineStatus("当前值 -", 205);
        _enemyAttributeValue = AttributeInput(150);
        _modifyEnemyButton = Theme.CommandButton("应用绝对值", Theme.Blue, 112);
        _modifyEnemyButton.Margin = new Padding(0, 23, 8, 0);
        _enemyAttribute.SelectedIndexChanged += (_, _) => AttributeSelectionChanged(
            _enemyAttribute,
            _enemyCurrentValue,
            _enemyAttributeValue);
        _modifyEnemyButton.Click += async (_, _) => await ModifyEnemyAsync();
        FlowLayoutPanel editor = HorizontalFlow(true);
        editor.Controls.AddRange(new Control[]
        {
            Field("属性", _enemyAttribute, 200),
            Field("当前 / 基础", _enemyCurrentValue, 215),
            Field("新绝对值", _enemyAttributeValue, 160),
            _modifyEnemyButton
        });
        layout.Controls.Add(editor, 0, 3);

        _entityQueryControls.Add(_enemyRefreshButton);
        AddMutationControls(_enemyAttribute, _enemyAttributeValue, _modifyEnemyButton);
        return layout;
    }

    private TabPage BuildSpawnPage()
    {
        TabPage page = CreateTabPage("生成");
        TableLayoutPanel layout = PageLayout(2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        FlowLayoutPanel toolbar = HorizontalFlow();
        Button refresh = QuietButton("刷新怪物目录", 120);
        refresh.Click += async (_, _) => await RefreshCatalogAsync();
        Label hint = InlineStatus("坐标范围由插件目录返回，生成数量最多 10 个", 520);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(hint);
        layout.Controls.Add(toolbar, 0, 0);

        _enemyCatalog = CatalogPicker(340);
        _enemyCatalog.SelectedItemChanged += (_, _) => ApplyAvailability();
        _enemyLevel = IntegerInput(1, 200, 1, 90);
        _enemyCount = IntegerInput(1, 10, 1, 82);
        _spawnX = CoordinateInput();
        _spawnY = CoordinateInput();
        _spawnZ = CoordinateInput();
        _capturePointButton = Theme.CommandButton("从游戏选点", Theme.Blue, 118);
        _cancelCaptureButton = QuietButton("取消选点", 88);
        _capturePointButton.Margin = new Padding(0, 23, 8, 0);
        _cancelCaptureButton.Margin = new Padding(0, 25, 8, 0);
        _capturePointButton.Click += async (_, _) => await SetSpawnPointCaptureAsync(true);
        _cancelCaptureButton.Click += async (_, _) => await SetSpawnPointCaptureAsync(false);
        _toolTip.SetToolTip(_capturePointButton, "进入游戏后按住 Alt 并单击鼠标左键，选择怪物生成位置。");
        _captureStatusLabel = InlineStatus("选点状态：未启动", 760);
        _captureStatusLabel.Dock = DockStyle.Fill;
        _spawnStatusLabel = InlineStatus("生成状态：尚未执行", 760);
        _spawnStatusLabel.Dock = DockStyle.Fill;
        _spawnEnemyButton = Theme.CommandButton("生成怪物", Theme.Amber, 112);
        _spawnEnemyButton.Margin = new Padding(0, 3, 8, 0);
        _spawnEnemyButton.Click += async (_, _) => await SpawnEnemyAsync();

        TableLayoutPanel spawnEditor = PageLayout(5);
        spawnEditor.Padding = new Padding(0, 8, 0, 0);
        spawnEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        spawnEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        spawnEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        spawnEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        spawnEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        FlowLayoutPanel catalogControls = HorizontalFlow(true);
        catalogControls.Controls.AddRange(new Control[]
        {
            CatalogField("怪物（中文名 / ID）", _enemyCatalog, 350),
            Field("等级", _enemyLevel, 100),
            Field("数量", _enemyCount, 92)
        });
        spawnEditor.Controls.Add(catalogControls, 0, 0);

        FlowLayoutPanel pointControls = HorizontalFlow(true);
        pointControls.Controls.AddRange(new Control[]
        {
            Field("X", _spawnX, 130),
            Field("Y", _spawnY, 130),
            Field("Z", _spawnZ, 130),
            _capturePointButton,
            _cancelCaptureButton
        });
        spawnEditor.Controls.Add(pointControls, 0, 1);
        spawnEditor.Controls.Add(_captureStatusLabel, 0, 2);
        spawnEditor.Controls.Add(Divider(), 0, 3);

        FlowLayoutPanel spawnAction = HorizontalFlow(true);
        spawnAction.Padding = new Padding(0, 12, 0, 0);
        spawnAction.Controls.Add(_spawnEnemyButton);
        spawnAction.Controls.Add(_spawnStatusLabel);
        spawnEditor.Controls.Add(spawnAction, 0, 4);
        layout.Controls.Add(FlatSection("在指定世界坐标生成怪物", spawnEditor, drawBottomLine: false), 0, 1);

        _catalogQueryControls.Add(refresh);
        AddMutationControls(
            _enemyCatalog, _enemyLevel, _enemyCount, _spawnX, _spawnY, _spawnZ,
            _capturePointButton, _cancelCaptureButton, _spawnEnemyButton);
        page.Controls.Add(layout);
        return page;
    }

    private async Task EnableCheckChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _enableCheck.Checked;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetEnabled,
            new JObject { ["enabled"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_enableCheck, !requested);
            return;
        }

        ApplyStateData(response.Data);
        if (requested)
        {
            await RefreshCatalogAsync(announce: false);
        }
    }

    private async Task RefreshCatalogAsync(bool announce = true)
    {
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryCatalog, null, announce);
        if (response?.Success != true || response.Data == null) return;

        _activeCatalogIconKeys.Clear();
        PopulateCatalog(_vehicleCatalog, response.Data["vehicles"] as JArray);
        PopulateCatalog(_enchantmentCatalog, response.Data["enchantments"] as JArray);
        PopulateCatalog(_disposableCatalog, response.Data["disposables"] as JArray);
        PopulateCatalog(_relicCatalog, response.Data["relics"] as JArray);
        PopulateCatalog(_enemyCatalog, response.Data["enemies"] as JArray);
        PopulateCatalog(_catapultCatalog, response.Data["catapultPoints"] as JArray);
        DisposeUnusedCatalogIcons();
        ApplyCatalogLimits(response.Data["limits"] as JObject);
        _catalogSummary.Text = $"战车 {_vehicleCatalog.ItemCount} / 附魔 {_enchantmentCatalog.ItemCount} / 消耗品 {_disposableCatalog.ItemCount} / 遗物 {_relicCatalog.ItemCount} / 弹射点 {_catapultCatalog.ItemCount} / 怪物 {_enemyCatalog.ItemCount}";
    }

    private async Task GrantVehicleAsync()
    {
        if (!TryCatalogSelection(_vehicleCatalog, "请选择战车。", out CatalogPickerItem? vehicle)) return;
        JArray enchantments = new();
        foreach (DataGridViewRow row in _enchantmentGrid.Rows)
        {
            if (row.Tag is not EnchantmentSelection selection) continue;
            enchantments.Add(new JObject
            {
                ["enchantmentId"] = selection.Item.Id,
                ["level"] = selection.Level
            });
        }

        JObject arguments = new()
        {
            ["vehicleId"] = vehicle!.Id,
            ["count"] = Decimal.ToInt32(_vehicleCount.Value),
            ["enchantments"] = enchantments
        };
        await ExecuteCommandAsync(CheatCommands.GrantVehicle, arguments);
    }

    private void AddOrUpdateEnchantment()
    {
        if (!TryCatalogSelection(_enchantmentCatalog, "请选择要添加的附魔。", out CatalogPickerItem? item)) return;
        int level = Decimal.ToInt32(_enchantmentLevel.Value);
        DataGridViewRow? existing = _enchantmentGrid.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(row => row.Tag is EnchantmentSelection selection
                                   && string.Equals(selection.Item.Id, item!.Id, StringComparison.Ordinal));
        EnchantmentSelection next = new(item!, level);
        if (existing != null)
        {
            existing.Tag = next;
            existing.Cells["icon"].Value = item!.Icon;
            existing.Cells["name"].Value = item.DisplayName;
            existing.Cells["id"].Value = item.Id;
            existing.Cells["level"].Value = level;
            existing.Selected = true;
            _enchantmentGrid.CurrentCell = existing.Cells["name"];
            UpdateEnchantmentSummary("已更新附魔等级");
            return;
        }

        if (_enchantmentGrid.Rows.Count >= _maxEnchantmentsPerVehicle)
        {
            ShowLocalError($"一辆战车最多添加 {_maxEnchantmentsPerVehicle} 个附魔。");
            return;
        }

        int rowIndex = _enchantmentGrid.Rows.Add(item!.Icon, item.DisplayName, item.Id, level);
        DataGridViewRow row = _enchantmentGrid.Rows[rowIndex];
        row.Tag = next;
        row.Selected = true;
        _enchantmentGrid.CurrentCell = row.Cells["name"];
        UpdateEnchantmentSummary("已添加附魔");
    }

    private void RemoveSelectedEnchantment()
    {
        if (_enchantmentGrid.SelectedRows.Count == 0) return;
        _enchantmentGrid.Rows.Remove(_enchantmentGrid.SelectedRows[0]);
        SelectFirstRowWhenNeeded(_enchantmentGrid);
        UpdateEnchantmentSummary();
    }

    private void ClearEnchantments()
    {
        _enchantmentGrid.Rows.Clear();
        UpdateEnchantmentSummary();
    }

    private void UpdateEnchantmentSummary(string? action = null)
    {
        _enchantmentSummary.Text = string.IsNullOrWhiteSpace(action)
            ? $"已选 {_enchantmentGrid.Rows.Count} / {_maxEnchantmentsPerVehicle}"
            : $"{action} · {_enchantmentGrid.Rows.Count} / {_maxEnchantmentsPerVehicle}";
        _enchantmentSummary.ForeColor = Theme.Muted;
        ApplyAvailability();
    }

    private async Task GrantDisposableAsync()
    {
        if (!TryCatalogSelection(_disposableCatalog, "请选择消耗品。", out CatalogPickerItem? item)) return;
        await ExecuteCommandAsync(
            CheatCommands.GrantDisposable,
            new JObject
            {
                ["disposableId"] = item!.Id,
                ["count"] = Decimal.ToInt32(_disposableCount.Value)
            });
    }

    private async Task GrantRelicAsync()
    {
        if (!TryCatalogSelection(_relicCatalog, "请选择遗物。", out CatalogPickerItem? item)) return;
        await ExecuteCommandAsync(
            CheatCommands.GrantRelic,
            new JObject { ["relicId"] = item!.Id });
    }

    private async Task GrantCatapultPointAsync()
    {
        if (!TryCatalogSelection(_catapultCatalog, "请选择弹射点。", out CatalogPickerItem? item)) return;
        await ExecuteCommandAsync(
            CheatCommands.GrantCatapultPoint,
            new JObject
            {
                ["disposableId"] = item!.Id,
                ["count"] = Decimal.ToInt32(_catapultCount.Value)
            });
    }

    private async Task BaseGodModeChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _baseGodModeCheck.Checked;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetBaseGodMode,
            new JObject { ["enabled"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_baseGodModeCheck, !requested);
            return;
        }

        bool accepted = response.Data?.Value<bool?>("requested") ?? requested;
        SetCheckedSilently(_baseGodModeCheck, accepted);
    }

    private async Task EnemyIdOverlayChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _enemyIdOverlayCheck.Checked;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetEnemyIdOverlay,
            new JObject { ["visible"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_enemyIdOverlayCheck, !requested);
            return;
        }

        SetCheckedSilently(_enemyIdOverlayCheck, response.Data?.Value<bool?>("visible") ?? requested);
    }

    private async Task ConfirmAndExecuteAsync(string title, string prompt, string command)
    {
        DialogResult confirmation = MessageBox.Show(
            this,
            prompt,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        await ExecuteCommandAsync(command, new JObject());
    }

    private async Task RefreshVehiclesAsync(bool announce = true)
    {
        string previous = SelectedEntity(_vehicleGrid)?.Value<int?>("vehicleId")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryVehicles, null, announce);
        if (response?.Success != true) return;
        PopulateVehicleGrid(response.Data?["vehicles"] as JArray, previous);
    }

    private async Task RefreshEnemiesAsync(bool announce = true)
    {
        string previous = SelectedEntity(_enemyGrid)?.Value<string>("runtimeId") ?? string.Empty;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryEnemies, null, announce);
        if (response?.Success != true) return;
        PopulateEnemyGrid(response.Data?["enemies"] as JArray, previous);
    }

    private async Task ModifyVehicleAsync()
    {
        JObject? vehicle = SelectedEntity(_vehicleGrid);
        AttributeItem? attribute = _vehicleAttribute.SelectedItem as AttributeItem;
        int? vehicleId = vehicle?.Value<int?>("vehicleId");
        if (!vehicleId.HasValue || attribute == null)
        {
            ShowLocalError("请选择战车和要修改的属性。");
            return;
        }

        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.ModifyVehicle,
            new JObject
            {
                ["vehicleId"] = vehicleId.Value,
                ["attributeId"] = attribute.Id,
                ["value"] = Decimal.ToDouble(_vehicleAttributeValue.Value)
            });
        if (response?.Success == true)
        {
            string successMessage = _lastMessage;
            await RefreshVehiclesAsync(announce: false);
            RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task ModifyEnemyAsync()
    {
        JObject? enemy = SelectedEntity(_enemyGrid);
        AttributeItem? attribute = _enemyAttribute.SelectedItem as AttributeItem;
        string runtimeId = enemy?.Value<string>("runtimeId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runtimeId) || attribute == null)
        {
            ShowLocalError("请选择敌人和要修改的属性。");
            return;
        }

        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.ModifyEnemy,
            new JObject
            {
                ["runtimeId"] = runtimeId,
                ["attributeId"] = attribute.Id,
                ["value"] = Decimal.ToDouble(_enemyAttributeValue.Value)
            });
        if (response?.Success == true)
        {
            string successMessage = _lastMessage;
            await RefreshEnemiesAsync(announce: false);
            RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task SpawnEnemyAsync()
    {
        if (!TryCatalogSelection(_enemyCatalog, "请选择要生成的怪物。", out CatalogPickerItem? enemy)) return;
        int requested = Decimal.ToInt32(_enemyCount.Value);
        _spawnStatusLabel.Text = $"生成状态：正在请求生成 {requested} 个怪物...";
        _spawnStatusLabel.ForeColor = Theme.Blue;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SpawnEnemy,
            new JObject
            {
                ["enemyId"] = enemy!.Id,
                ["level"] = Decimal.ToInt32(_enemyLevel.Value),
                ["count"] = requested,
                ["x"] = Decimal.ToDouble(_spawnX.Value),
                ["y"] = Decimal.ToDouble(_spawnY.Value),
                ["z"] = Decimal.ToDouble(_spawnZ.Value)
            });
        if (IsDisposed) return;
        int accepted = response?.Data?.Value<int?>("requested") ?? requested;
        int spawned = response?.Data?.Value<int?>("spawned") ?? (response?.Success == true ? accepted : 0);
        if (response?.Success == true && spawned >= accepted)
        {
            _spawnStatusLabel.Text = $"生成状态：已生成 {spawned} / {accepted}";
            _spawnStatusLabel.ForeColor = Theme.Teal;
        }
        else if (spawned > 0)
        {
            _spawnStatusLabel.Text = $"生成状态：部分成功，已生成 {spawned} / {accepted}";
            _spawnStatusLabel.ForeColor = Theme.Amber;
        }
        else
        {
            _spawnStatusLabel.Text = "生成状态：失败，未生成怪物";
            _spawnStatusLabel.ForeColor = Theme.Red;
        }
    }

    private async Task SetSpawnPointCaptureAsync(bool enabled)
    {
        long operationEpoch = ++_captureEpoch;
        _spawnCaptureState = enabled ? "arming" : "cancelling";
        _captureStatusLabel.Text = enabled ? "选点状态：正在启动..." : "选点状态：正在取消...";
        _captureStatusLabel.ForeColor = Theme.Blue;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetSpawnPointCapture,
            new JObject { ["enabled"] = enabled });
        if (IsDisposed || operationEpoch != _captureEpoch) return;
        if (response?.Success != true)
        {
            _capturePollTimer.Stop();
            _spawnCaptureState = "failed";
            _captureStatusLabel.Text = enabled ? "选点状态：启动失败" : "选点状态：取消失败";
            _captureStatusLabel.ForeColor = Theme.Red;
            ApplyAvailability();
            return;
        }

        JObject? capture = ExtractSpawnPointCapture(response.Data);
        if (capture != null)
        {
            ApplySpawnPointCapture(capture);
        }
        else
        {
            ApplySpawnPointCapture(new JObject
            {
                ["state"] = enabled ? "armed" : "cancelled",
                ["message"] = enabled ? "等待游戏内选点" : "已取消选点"
            });
        }
    }

    private async Task CancelSpawnPointCaptureOnCloseAsync()
    {
        try
        {
            await _sendCommand(
                CheatCommands.SetSpawnPointCapture,
                new JObject { ["enabled"] = false });
        }
        catch
        {
            // The plugin also expires an abandoned capture request. Closing the
            // window must never be delayed by a best-effort cleanup failure.
        }
    }

    private async void CapturePollTimerTick(object? sender, EventArgs eventArgs)
    {
        if (_capturePollInProgress || _busy || !string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase)) return;
        string pollSessionKey = _sessionKey;
        long pollEpoch = _captureEpoch;
        _capturePollInProgress = true;
        try
        {
            ControlResponse? response = await _sendCommand(CheatCommands.QueryState, null);
            if (IsDisposed
                || pollEpoch != _captureEpoch
                || !string.Equals(_sessionKey, pollSessionKey, StringComparison.Ordinal)) return;
            if (response == null)
            {
                SetCapturePollingError("插件没有返回选点状态。");
                return;
            }

            if (response.Status != null)
            {
                UpdateSession(_trusted, response.Hello ?? _hello, response.Status);
            }

            ApplyStateData(response.Data);
            if (!response.Success)
            {
                SetCapturePollingError(string.IsNullOrWhiteSpace(response.Message)
                    ? "读取选点状态失败。"
                    : response.Message);
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed
                && pollEpoch == _captureEpoch
                && string.Equals(_sessionKey, pollSessionKey, StringComparison.Ordinal))
            {
                SetCapturePollingError("读取选点状态失败：" + exception.Message);
            }
        }
        finally
        {
            _capturePollInProgress = false;
            if (!IsDisposed) ApplyAvailability();
        }
    }

    private void SetCapturePollingError(string message)
    {
        _capturePollTimer.Stop();
        _spawnCaptureState = "failed";
        _captureStatusLabel.Text = "选点状态：" + message;
        _captureStatusLabel.ForeColor = Theme.Red;
    }

    private static JObject? ExtractSpawnPointCapture(JObject? data)
    {
        if (data == null) return null;
        if (data["spawnPointCapture"] is JObject nested) return nested;
        return data["state"] != null ? data : null;
    }

    private void ApplySpawnPointCapture(JObject capture)
    {
        string state = (capture.Value<string>("state") ?? "idle").Trim().ToLowerInvariant();
        string message = capture.Value<string>("message") ?? string.Empty;
        _spawnCaptureState = state;
        switch (state)
        {
            case "armed":
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：等待游戏内 Alt + 左键..."
                    : "选点状态：" + message;
                _captureStatusLabel.ForeColor = Theme.Amber;
                if (!_capturePollTimer.Enabled) _capturePollTimer.Start();
                break;
            case "captured":
                _capturePollTimer.Stop();
                SetCoordinateValue(_spawnX, ToDecimal(capture["x"], _spawnX.Value));
                SetCoordinateValue(_spawnY, ToDecimal(capture["y"], _spawnY.Value));
                SetCoordinateValue(_spawnZ, ToDecimal(capture["z"], _spawnZ.Value));
                _captureStatusLabel.Text = $"选点状态：已捕获 {FormatNumber(_spawnX.Value)} / {FormatNumber(_spawnY.Value)} / {FormatNumber(_spawnZ.Value)}";
                _captureStatusLabel.ForeColor = Theme.Teal;
                break;
            case "failed":
            case "expired":
                _capturePollTimer.Stop();
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：捕获失败"
                    : "选点状态：" + message;
                _captureStatusLabel.ForeColor = Theme.Red;
                break;
            case "cancelled":
            case "disabled":
            case "idle":
            default:
                _capturePollTimer.Stop();
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：未启动"
                    : "选点状态：" + message;
                _captureStatusLabel.ForeColor = Theme.Muted;
                break;
        }

        ApplyAvailability();
    }

    private void ResetSpawnPointCapture(string statusText)
    {
        _captureEpoch++;
        _capturePollTimer.Stop();
        _spawnCaptureState = "idle";
        if (_captureStatusLabel == null) return;
        _captureStatusLabel.Text = statusText;
        _captureStatusLabel.ForeColor = Theme.Muted;
    }

    private static void SetCoordinateValue(NumericUpDown input, decimal value)
    {
        input.Value = Math.Clamp(value, input.Minimum, input.Maximum);
    }

    private async Task<ControlResponse?> ExecuteCommandAsync(
        string command,
        JObject? arguments,
        bool announce = true)
    {
        if (_busy) return null;
        string previousMessage = _lastMessage;
        bool previousMessageIsError = _lastMessageIsError;
        _busy = true;
        _lastMessage = "正在执行命令...";
        _lastMessageIsError = false;
        ApplyAvailability();
        try
        {
            ControlResponse? response = await _sendCommand(command, arguments);
            if (IsDisposed) return response;
            if (response == null)
            {
                ShowLocalError("插件没有返回有效响应。");
                return null;
            }

            if (response.Status != null)
            {
                UpdateSession(_trusted, response.Hello ?? _hello, response.Status);
            }

            ApplyStateData(response.Data);
            if (!response.Success)
            {
                ShowLocalError(string.IsNullOrWhiteSpace(response.Message) ? "作弊命令执行失败。" : response.Message);
            }
            else if (announce)
            {
                _lastMessage = string.IsNullOrWhiteSpace(response.Message) ? "命令执行成功。" : response.Message;
                _lastMessageIsError = false;
            }
            else
            {
                _lastMessage = previousMessage;
                _lastMessageIsError = previousMessageIsError;
            }

            return response;
        }
        catch (Exception exception)
        {
            if (!IsDisposed) ShowLocalError("发送作弊命令失败：" + exception.Message);
            return null;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) ApplyAvailability();
        }
    }

    private void ApplyStateData(JObject? data)
    {
        if (data == null) return;
        if (ReadBoolean(data["outcomeUnknown"]) == true) _writeOutcomeUnknown = true;
        _synchronizing = true;
        try
        {
            bool? enabled = ReadBoolean(data["enabled"]);
            if (enabled.HasValue) _enableCheck.Checked = enabled.Value;
            bool? overlay = ReadBoolean(data["enemyIdsVisible"]) ?? ReadBoolean(data["visible"]);
            if (overlay.HasValue) _enemyIdOverlayCheck.Checked = overlay.Value;
            bool? godMode = ReadBoolean(data["baseGodMode"]);
            if (godMode.HasValue) _baseGodModeCheck.Checked = godMode.Value;
        }
        finally
        {
            _synchronizing = false;
        }

        JObject? capture = ExtractSpawnPointCapture(data);
        if (capture != null) ApplySpawnPointCapture(capture);
    }

    private void ApplyAvailability()
    {
        if (_enableCheck == null) return;
        bool available = _trusted && _status?.CheatAvailable == true;
        bool runConflict = _status?.RunState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused;
        bool canMutate = available
                         && _status?.CheatModeEnabled == true
                         && !_writeOutcomeUnknown
                         && !_busy;
        bool canQueryCatalog = available && !_busy;
        bool canQueryEntities = available && _status?.CheatModeEnabled == true && !_busy;

        _enableCheck.Enabled = available && !runConflict && !_writeOutcomeUnknown && !_busy;
        foreach (Control control in _mutationControls) control.Enabled = canMutate;
        foreach (Control control in _catalogQueryControls) control.Enabled = canQueryCatalog;
        foreach (Control control in _entityQueryControls) control.Enabled = canQueryEntities;

        if (_addEnchantmentButton != null)
        {
            bool selectedAlready = _enchantmentCatalog.SelectedCatalogItem != null
                                   && _enchantmentGrid.Rows.Cast<DataGridViewRow>().Any(row =>
                                       row.Tag is EnchantmentSelection selection
                                       && string.Equals(
                                           selection.Item.Id,
                                           _enchantmentCatalog.SelectedCatalogItem.Id,
                                           StringComparison.Ordinal));
            _addEnchantmentButton.Enabled = canMutate
                                            && _enchantmentCatalog.SelectedCatalogItem != null
                                            && (selectedAlready || _enchantmentGrid.Rows.Count < _maxEnchantmentsPerVehicle);
            _removeEnchantmentButton.Enabled = canMutate && _enchantmentGrid.SelectedRows.Count > 0;
            _clearEnchantmentsButton.Enabled = canMutate && _enchantmentGrid.Rows.Count > 0;
            _grantVehicleButton.Enabled = canMutate && _vehicleCatalog.SelectedCatalogItem != null;
            _grantDisposableButton.Enabled = canMutate && _disposableCatalog.SelectedCatalogItem != null;
            _grantRelicButton.Enabled = canMutate && _relicCatalog.SelectedCatalogItem != null;
            _grantCatapultButton.Enabled = canMutate && _catapultCatalog.SelectedCatalogItem != null;
        }

        if (_capturePointButton != null)
        {
            bool captureArmed = string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase);
            _capturePointButton.Enabled = canMutate && !captureArmed;
            _cancelCaptureButton.Enabled = canMutate && captureArmed;
            _spawnEnemyButton.Enabled = canMutate && _enemyCatalog.SelectedCatalogItem != null;
        }

        if (_modifyVehicleButton != null)
        {
            _modifyVehicleButton.Enabled = canMutate
                                           && SelectedEntity(_vehicleGrid) != null
                                           && _vehicleAttribute.SelectedItem is AttributeItem;
        }

        if (_modifyEnemyButton != null)
        {
            _modifyEnemyButton.Enabled = canMutate
                                         && SelectedEntity(_enemyGrid) != null
                                         && _enemyAttribute.SelectedItem is AttributeItem;
        }

        RenderStatusBanner(available, runConflict);
    }

    private void RenderStatusBanner(bool available, bool runConflict)
    {
        Color rail;
        if (_busy)
        {
            _statusTitle.Text = "正在执行作弊命令";
            rail = Theme.Blue;
            _statusBanner.BackColor = Color.FromArgb(230, 239, 247);
        }
        else if (_writeOutcomeUnknown)
        {
            _statusTitle.Text = "写命令结果未知 / 已冻结后续修改";
            rail = Theme.Red;
            _statusBanner.BackColor = Color.FromArgb(250, 233, 233);
        }
        else if (!_trusted)
        {
            _statusTitle.Text = "未连接到可信测试会话";
            rail = Theme.Red;
            _statusBanner.BackColor = Color.FromArgb(250, 233, 233);
        }
        else if (!available)
        {
            _statusTitle.Text = "当前构建不支持作弊工具";
            rail = Theme.Red;
            _statusBanner.BackColor = Color.FromArgb(250, 233, 233);
        }
        else if (runConflict)
        {
            _statusTitle.Text = "自动游玩运行或暂停时不能启用作弊";
            rail = Theme.Amber;
            _statusBanner.BackColor = Color.FromArgb(246, 239, 219);
        }
        else if (_status?.CheatModeEnabled == true)
        {
            _statusTitle.Text = _status.CheatUsed
                ? _status.CheatActionCount > 0
                    ? $"作弊模式已启用 / 本进程已尝试 {_status.CheatActionCount} 次修改"
                    : "作弊模式已启用 / 当前 QA 档有历史作弊标记"
                : "作弊模式已启用";
            rail = Theme.Amber;
            _statusBanner.BackColor = Color.FromArgb(246, 239, 219);
        }
        else if (_status?.CheatUsed == true)
        {
            _statusTitle.Text = "当前 QA 档已被作弊修改 / 只能继续作弊测试";
            rail = Theme.Amber;
            _statusBanner.BackColor = Color.FromArgb(246, 239, 219);
        }
        else
        {
            _statusTitle.Text = "作弊功能可用 / 尚未启用";
            rail = Theme.Teal;
            _statusBanner.BackColor = Color.FromArgb(230, 242, 241);
        }

        if (_lastMessageIsError)
        {
            rail = Theme.Red;
            _statusBanner.BackColor = Color.FromArgb(250, 233, 233);
        }

        _statusTitle.ForeColor = rail;
        Control? stateRail = _statusBanner.Controls.Find("StateRail", false).FirstOrDefault();
        if (stateRail != null) stateRail.BackColor = rail;
        _statusDetail.Text = BuildStatusDetail(available);
    }

    private string BuildStatusDetail(bool available)
    {
        if (!string.IsNullOrWhiteSpace(_lastMessage)) return _lastMessage;
        if (!_trusted) return "请先从 Manager 启动并连接已验证的隔离测试包。";
        if (!available)
        {
            string reason = _status?.CheatAvailabilityReason ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reason)) reason = _hello?.CheatAvailabilityReason ?? string.Empty;
            return string.IsNullOrWhiteSpace(reason) ? "插件未提供作弊运行时合同。" : reason;
        }

        if (_status?.CheatUsed == true)
        {
            return "该 QA 档已有持久作弊污染标记；普通自动游玩已禁用，请为干净基线使用新的 QA 配置名称。";
        }
        return "启用后可以执行资源、战斗、属性和怪物生成命令。";
    }

    private void ClearSessionData()
    {
        _lastMessage = string.Empty;
        _lastMessageIsError = false;
        _writeOutcomeUnknown = false;
        _captureEpoch++;
        _capturePollTimer.Stop();
        _spawnCaptureState = "idle";
        _vehicleCatalog?.ClearItems();
        _enchantmentCatalog?.ClearItems();
        _disposableCatalog?.ClearItems();
        _relicCatalog?.ClearItems();
        _enemyCatalog?.ClearItems();
        _catapultCatalog?.ClearItems();
        _enchantmentGrid?.Rows.Clear();
        _vehicleGrid?.Rows.Clear();
        _enemyGrid?.Rows.Clear();
        DisposeCatalogIcons();
        _maxEnchantmentsPerVehicle = 5;
        if (_catalogSummary != null) _catalogSummary.Text = "尚未读取资源目录";
        if (_enchantmentSummary != null) _enchantmentSummary.Text = "已选 0 / 5";
        if (_vehicleSummary != null) _vehicleSummary.Text = "尚未读取战车";
        if (_enemySummary != null) _enemySummary.Text = "尚未读取敌人";
        if (_captureStatusLabel != null)
        {
            _captureStatusLabel.Text = "选点状态：未启动";
            _captureStatusLabel.ForeColor = Theme.Muted;
        }
        if (_spawnStatusLabel != null)
        {
            _spawnStatusLabel.Text = "生成状态：尚未执行";
            _spawnStatusLabel.ForeColor = Theme.Muted;
        }
        if (_baseGodModeCheck != null) SetCheckedSilently(_baseGodModeCheck, false);
        if (_enemyIdOverlayCheck != null) SetCheckedSilently(_enemyIdOverlayCheck, false);
    }

    private void PopulateCatalog(CatalogPickerControl picker, JArray? items)
    {
        List<CatalogPickerItem> catalogItems = new();
        foreach (JObject item in items?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string id = item.Value<string>("id") ?? item["id"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;
            string name = item.Value<string>("name") ?? string.Empty;
            string fallbackName = item.Value<string>("fallbackName") ?? string.Empty;
            string iconFile = item.Value<string>("iconFile") ?? string.Empty;
            string iconSha256 = item.Value<string>("iconSha256") ?? string.Empty;
            IReadOnlyList<string> tags = (item["tags"] as JArray)?
                .Values<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray() ?? Array.Empty<string>();
            catalogItems.Add(new CatalogPickerItem(
                id,
                name,
                fallbackName,
                TryLoadCatalogIcon(iconFile, iconSha256),
                tags));
        }

        picker.SetItems(catalogItems);
        ApplyAvailability();
    }

    private Image? TryLoadCatalogIcon(string iconFile, string iconSha256)
    {
        if (string.IsNullOrWhiteSpace(iconFile) || string.IsNullOrWhiteSpace(iconSha256)) return null;
        string artifactRoot = _hello?.ArtifactRoot ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artifactRoot)
            || !Path.IsPathFullyQualified(artifactRoot)
            || Path.IsPathRooted(iconFile))
        {
            return null;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactRoot));
            string candidate = Path.GetFullPath(Path.Combine(root, iconFile));
            string relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, ".", StringComparison.Ordinal)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
            DirectoryInfo? directory = Directory.GetParent(candidate);
            while (directory != null && !string.Equals(
                       Path.TrimEndingDirectorySeparator(directory.FullName),
                       root,
                       StringComparison.OrdinalIgnoreCase))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                directory = directory.Parent;
            }
            if (directory == null) return null;
            if (iconSha256.Length != 64) return null;

            byte[] expectedHash;
            try
            {
                expectedHash = Convert.FromHexString(iconSha256);
            }
            catch (FormatException)
            {
                return null;
            }

            string cacheKey = candidate + "|" + iconSha256.ToUpperInvariant();
            _activeCatalogIconKeys.Add(cacheKey);
            if (_iconCache.TryGetValue(cacheKey, out Image? cached)) return cached;

            byte[] bytes;
            using (FileStream file = new(
                       candidate,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                if (file.Length <= 0 || file.Length > 4 * 1024 * 1024) return null;
                bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
            }
            byte[] actualHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) return null;

            using MemoryStream stream = new(bytes, writable: false);
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (source.Width <= 0 || source.Height <= 0 || source.Width > 1024 || source.Height > 1024) return null;
            Image image = new Bitmap(source);
            _iconCache[cacheKey] = image;
            return image;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OutOfMemoryException
                                          or System.Security.SecurityException
                                          or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private void DisposeCatalogIcons()
    {
        foreach (Image image in _iconCache.Values) image.Dispose();
        _iconCache.Clear();
        _activeCatalogIconKeys.Clear();
    }

    private void DisposeUnusedCatalogIcons()
    {
        foreach (string key in _iconCache.Keys
                     .Where(key => !_activeCatalogIconKeys.Contains(key))
                     .ToArray())
        {
            _iconCache[key].Dispose();
            _iconCache.Remove(key);
        }
    }

    private void ApplyCatalogLimits(JObject? limits)
    {
        if (limits == null) return;
        SetMaximum(_vehicleCount, limits.Value<int?>("maxGrantCount"));
        SetMaximum(_disposableCount, limits.Value<int?>("maxGrantCount"));
        SetMaximum(_catapultCount, limits.Value<int?>("maxGrantCount"));
        SetMaximum(_enchantmentLevel, limits.Value<int?>("maxEnchantmentLevel"));
        SetMaximum(_enemyLevel, limits.Value<int?>("maxEnemyLevel"));
        SetMaximum(_enemyCount, limits.Value<int?>("maxSpawnCount"));
        _maxEnchantmentsPerVehicle = Math.Clamp(limits.Value<int?>("maxEnchantmentsPerVehicle") ?? 5, 0, 64);
        while (_enchantmentGrid.Rows.Count > _maxEnchantmentsPerVehicle)
        {
            _enchantmentGrid.Rows.RemoveAt(_enchantmentGrid.Rows.Count - 1);
        }
        UpdateEnchantmentSummary();
        decimal coordinateMaximum = ToDecimal(limits["maxCoordinateMagnitude"], 10_000m);
        coordinateMaximum = Math.Max(1m, Math.Abs(coordinateMaximum));
        foreach (NumericUpDown input in new[] { _spawnX, _spawnY, _spawnZ })
        {
            input.Minimum = -coordinateMaximum;
            input.Maximum = coordinateMaximum;
        }
    }

    private void PopulateVehicleGrid(JArray? vehicles, string selectedVehicleId)
    {
        _loadingEntities = true;
        _vehicleGrid.SuspendLayout();
        try
        {
            _vehicleGrid.Rows.Clear();
            foreach (JObject vehicle in vehicles?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int vehicleId = vehicle.Value<int?>("vehicleId") ?? 0;
                int rowIndex = _vehicleGrid.Rows.Add(
                    vehicleId,
                    vehicle.Value<string>("typeId") ?? "-",
                    vehicle.Value<string>("name") ?? "-",
                    vehicle.Value<int?>("level")?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    FormatPosition(vehicle["position"] as JObject));
                _vehicleGrid.Rows[rowIndex].Tag = vehicle;
                if (string.Equals(vehicleId.ToString(CultureInfo.InvariantCulture), selectedVehicleId, StringComparison.Ordinal))
                {
                    _vehicleGrid.Rows[rowIndex].Selected = true;
                    _vehicleGrid.CurrentCell = _vehicleGrid.Rows[rowIndex].Cells[0];
                }
            }

            SelectFirstRowWhenNeeded(_vehicleGrid);
            _vehicleSummary.Text = $"共 {_vehicleGrid.Rows.Count} 辆；修改命令使用战车 ID";
        }
        finally
        {
            _vehicleGrid.ResumeLayout();
            _loadingEntities = false;
        }

        EntitySelectionChanged(_vehicleGrid, _vehicleAttribute, _vehicleCurrentValue, _vehicleAttributeValue);
    }

    private void PopulateEnemyGrid(JArray? enemies, string selectedRuntimeId)
    {
        _loadingEntities = true;
        _enemyGrid.SuspendLayout();
        try
        {
            _enemyGrid.Rows.Clear();
            foreach (JObject enemy in enemies?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string runtimeId = enemy.Value<string>("runtimeId") ?? string.Empty;
                string health = FormatHealth(enemy["health"], enemy["healthMax"]);
                int rowIndex = _enemyGrid.Rows.Add(
                    runtimeId,
                    enemy.Value<string>("typeId") ?? "-",
                    enemy.Value<string>("name") ?? "-",
                    health,
                    FormatPosition(enemy["position"] as JObject));
                _enemyGrid.Rows[rowIndex].Tag = enemy;
                if (string.Equals(runtimeId, selectedRuntimeId, StringComparison.Ordinal))
                {
                    _enemyGrid.Rows[rowIndex].Selected = true;
                    _enemyGrid.CurrentCell = _enemyGrid.Rows[rowIndex].Cells[0];
                }
            }

            SelectFirstRowWhenNeeded(_enemyGrid);
            _enemySummary.Text = $"共 {_enemyGrid.Rows.Count} 个；修改命令使用运行时 ID";
        }
        finally
        {
            _enemyGrid.ResumeLayout();
            _loadingEntities = false;
        }

        EntitySelectionChanged(_enemyGrid, _enemyAttribute, _enemyCurrentValue, _enemyAttributeValue);
    }

    private void EntitySelectionChanged(
        DataGridView grid,
        ComboBox attributeCombo,
        Label currentValue,
        NumericUpDown input)
    {
        if (_loadingEntities || attributeCombo == null) return;
        JObject? entity = SelectedEntity(grid);
        string selectedAttribute = (attributeCombo.SelectedItem as AttributeItem)?.Id ?? string.Empty;
        attributeCombo.BeginUpdate();
        try
        {
            attributeCombo.Items.Clear();
            foreach (JObject attribute in entity?["attributes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string id = attribute.Value<string>("id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;
                attributeCombo.Items.Add(AttributeItem.FromJson(attribute));
            }

            int matching = attributeCombo.Items.Cast<AttributeItem>()
                .Select((item, index) => new { item, index })
                .FirstOrDefault(pair => string.Equals(pair.item.Id, selectedAttribute, StringComparison.Ordinal))
                ?.index ?? -1;
            attributeCombo.SelectedIndex = matching >= 0 ? matching : attributeCombo.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            attributeCombo.EndUpdate();
        }

        AttributeSelectionChanged(attributeCombo, currentValue, input);
        ApplyAvailability();
    }

    private static void AttributeSelectionChanged(ComboBox combo, Label currentValue, NumericUpDown input)
    {
        if (combo.SelectedItem is not AttributeItem attribute)
        {
            currentValue.Text = "当前值 -";
            input.Value = 0;
            return;
        }

        decimal minimum = Math.Clamp(attribute.Minimum, DefaultAttributeMinimum, DefaultAttributeMaximum);
        decimal maximum = Math.Clamp(attribute.Maximum, DefaultAttributeMinimum, DefaultAttributeMaximum);
        if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
        input.Minimum = DefaultAttributeMinimum;
        input.Maximum = DefaultAttributeMaximum;
        input.Value = 0;
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.DecimalPlaces = string.Equals(attribute.Kind, "integer", StringComparison.OrdinalIgnoreCase) ? 0 : 3;
        input.Increment = input.DecimalPlaces == 0 ? 1 : 0.1m;
        input.Value = Math.Clamp(attribute.BaseValue, minimum, maximum);
        currentValue.Text = $"{FormatNumber(attribute.Value)} / {FormatNumber(attribute.BaseValue)}";
    }

    private void ShowLocalError(string message)
    {
        _lastMessage = message;
        _lastMessageIsError = true;
        if (_statusBanner != null) ApplyAvailability();
    }

    private void RestoreSuccessfulMessage(string message)
    {
        _lastMessage = message;
        _lastMessageIsError = false;
        ApplyAvailability();
    }

    private void SetCheckedSilently(CheckBox checkBox, bool value)
    {
        _synchronizing = true;
        try
        {
            checkBox.Checked = value;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private bool TryCatalogSelection(CatalogPickerControl picker, string error, out CatalogPickerItem? item)
    {
        item = picker.SelectedCatalogItem;
        if (item != null) return true;
        ShowLocalError(error);
        return false;
    }

    private void AddMutationControls(params Control[] controls) => _mutationControls.AddRange(controls);

    private static TabPage CreateTabPage(string text) => new(text)
    {
        BackColor = Theme.Surface,
        ForeColor = Theme.Ink,
        Padding = new Padding(16),
        UseVisualStyleBackColor = false
    };

    private static TableLayoutPanel PageLayout(int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = rows,
        Margin = Padding.Empty,
        BackColor = Theme.Surface
    };

    private static TableLayoutPanel EntityLayout()
    {
        TableLayoutPanel layout = PageLayout(4);
        layout.Padding = new Padding(0, 4, 0, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        return layout;
    }

    private static Control FlatSection(string heading, Control body, bool drawBottomLine = true)
    {
        TableLayoutPanel section = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = drawBottomLine ? 3 : 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 4),
            BackColor = Theme.Surface
        };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        section.Controls.Add(SectionHeading(heading), 0, 0);
        section.Controls.Add(body, 0, 1);
        if (drawBottomLine)
        {
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            section.Controls.Add(Divider(), 0, 2);
        }

        return section;
    }

    private static Label SectionHeading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Ink,
        Font = Theme.Body(10.5f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static FlowLayoutPanel HorizontalFlow(bool wrap = false) => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = wrap,
        AutoScroll = wrap,
        BackColor = Theme.Surface,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private static Control Field(string caption, Control input, int width)
    {
        TableLayoutPanel field = new()
        {
            Width = width,
            Height = 61,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 10, 0),
            Padding = Padding.Empty
        };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Label label = Theme.Caption(caption);
        label.Dock = DockStyle.Fill;
        label.Margin = Padding.Empty;
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private static Control CatalogField(string caption, CatalogPickerControl input, int width)
    {
        TableLayoutPanel field = new()
        {
            Width = width,
            Height = 72,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 10, 0),
            Padding = Padding.Empty
        };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label label = Theme.Caption(caption);
        label.Dock = DockStyle.Fill;
        label.Margin = Padding.Empty;
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private static CatalogPickerControl CatalogPicker(int width) => new()
    {
        Width = width,
        Height = 46
    };

    private static ComboBox CatalogCombo(int width) => new()
    {
        Width = width,
        Height = 30,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Font = Theme.Body(9f),
        IntegralHeight = false,
        MaxDropDownItems = 14
    };

    private static ComboBox AttributeCombo(int width) => CatalogCombo(width);

    private static NumericUpDown IntegerInput(decimal minimum, decimal maximum, decimal value, int width) => new()
    {
        Width = width,
        Height = 30,
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        DecimalPlaces = 0,
        ThousandsSeparator = true,
        TextAlign = HorizontalAlignment.Right,
        Font = Theme.Data(9f)
    };

    private static NumericUpDown AttributeInput(int width) => new()
    {
        Width = width,
        Height = 30,
        Minimum = DefaultAttributeMinimum,
        Maximum = DefaultAttributeMaximum,
        DecimalPlaces = 3,
        Increment = 0.1m,
        ThousandsSeparator = true,
        TextAlign = HorizontalAlignment.Right,
        Font = Theme.Data(9f)
    };

    private static NumericUpDown CoordinateInput() => new()
    {
        Width = 120,
        Height = 30,
        Minimum = -10_000,
        Maximum = 10_000,
        DecimalPlaces = 2,
        Increment = 0.5m,
        ThousandsSeparator = true,
        TextAlign = HorizontalAlignment.Right,
        Font = Theme.Data(9f)
    };

    private static CheckBox Toggle(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 30,
        ForeColor = Theme.Ink,
        Font = Theme.Body(9f),
        CheckAlign = ContentAlignment.MiddleLeft,
        TextAlign = ContentAlignment.MiddleLeft,
        Cursor = Cursors.Hand
    };

    private static Button QuietButton(string text, int width)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 30,
            BackColor = Color.White,
            ForeColor = Theme.Blue,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 10, 0),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Theme.Line;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(237, 244, 249);
        return button;
    }

    private static Label InlineStatus(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 34,
        ForeColor = Theme.Muted,
        Font = Theme.Data(8.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static Panel Divider() => new()
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = Theme.Line,
        Margin = Padding.Empty
    };

    private static DataGridView CreateVehicleGrid()
    {
        DataGridView grid = CreateGrid();
        AddGridColumn(grid, "vehicleId", "战车 ID", 90);
        AddGridColumn(grid, "typeId", "类型 ID", 150);
        AddGridColumn(grid, "name", "对象名称", 220, fill: true);
        AddGridColumn(grid, "level", "等级", 70);
        AddGridColumn(grid, "position", "位置 X / Y / Z", 190);
        return grid;
    }

    private static DataGridView CreateEnemyGrid()
    {
        DataGridView grid = CreateGrid();
        AddGridColumn(grid, "runtimeId", "运行时 ID", 120);
        AddGridColumn(grid, "typeId", "类型 ID", 150);
        AddGridColumn(grid, "name", "对象名称", 200, fill: true);
        AddGridColumn(grid, "health", "生命值", 130);
        AddGridColumn(grid, "position", "位置 X / Y / Z", 190);
        return grid;
    }

    private static DataGridView CreateEnchantmentGrid()
    {
        DataGridView grid = CreateGrid();
        grid.RowTemplate.Height = 36;
        grid.ColumnHeadersHeight = 30;
        DataGridViewImageColumn icon = new()
        {
            Name = "icon",
            HeaderText = string.Empty,
            Width = 44,
            MinimumWidth = 44,
            ReadOnly = true,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        grid.Columns.Add(icon);
        AddGridColumn(grid, "name", "附魔名称", 180, fill: true);
        AddGridColumn(grid, "id", "附魔 ID", 160);
        AddGridColumn(grid, "level", "等级", 64);
        return grid;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
        ColumnHeadersHeight = 28,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EnableHeadersVisualStyles = false,
        GridColor = Theme.Line,
        MultiSelect = false,
        RowHeadersVisible = false,
        RowTemplate = { Height = 25 },
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Theme.Ink,
            SelectionBackColor = Color.FromArgb(213, 234, 234),
            SelectionForeColor = Theme.Ink,
            Font = Theme.Data(8.5f)
        },
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Ink,
            ForeColor = Color.White,
            SelectionBackColor = Theme.Ink,
            SelectionForeColor = Color.White,
            Font = Theme.Body(8.5f, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        }
    };

    private static void AddGridColumn(DataGridView grid, string name, string header, int width, bool fill = false)
    {
        DataGridViewTextBoxColumn column = new()
        {
            Name = name,
            HeaderText = header,
            Width = width,
            MinimumWidth = Math.Min(width, 70),
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
        };
        if (fill) column.MinimumWidth = width;
        grid.Columns.Add(column);
    }

    private static JObject? SelectedEntity(DataGridView? grid)
    {
        if (grid == null || grid.SelectedRows.Count == 0) return null;
        return grid.SelectedRows[0].Tag as JObject;
    }

    private static void SelectFirstRowWhenNeeded(DataGridView grid)
    {
        if (grid.Rows.Count == 0 || grid.SelectedRows.Count > 0) return;
        grid.Rows[0].Selected = true;
        grid.CurrentCell = grid.Rows[0].Cells[0];
    }

    private static void SetMaximum(NumericUpDown input, int? maximum)
    {
        if (!maximum.HasValue || maximum.Value < input.Minimum) return;
        input.Maximum = maximum.Value;
        if (input.Value > input.Maximum) input.Value = input.Maximum;
    }

    private static string FormatPosition(JObject? position)
    {
        if (position == null) return "-";
        return $"{FormatNumber(ToDecimal(position["x"], 0))} / {FormatNumber(ToDecimal(position["y"], 0))} / {FormatNumber(ToDecimal(position["z"], 0))}";
    }

    private static string FormatHealth(JToken? health, JToken? maximum)
    {
        if (health == null || health.Type == JTokenType.Null) return "-";
        return $"{FormatNumber(ToDecimal(health, 0))} / {FormatNumber(ToDecimal(maximum, 0))}";
    }

    private static string FormatNumber(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static decimal ToDecimal(JToken? token, decimal fallback)
    {
        if (token == null) return fallback;
        return decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : fallback;
    }

    private static bool? ReadBoolean(JToken? token) => token?.Type == JTokenType.Boolean
        ? token.Value<bool>()
        : null;

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record EnchantmentSelection(CatalogPickerItem Item, int Level);

    private sealed record AttributeItem(
        string Id,
        string Name,
        string Kind,
        decimal Value,
        decimal BaseValue,
        decimal Minimum,
        decimal Maximum)
    {
        public static AttributeItem FromJson(JObject item) => new(
            item.Value<string>("id") ?? string.Empty,
            item.Value<string>("name") ?? item.Value<string>("id") ?? string.Empty,
            item.Value<string>("kind") ?? "float",
            ToDecimal(item["value"], 0),
            ToDecimal(item["baseValue"], 0),
            ToDecimal(item["minimum"], DefaultAttributeMinimum),
            ToDecimal(item["maximum"], DefaultAttributeMaximum));

        public override string ToString() => string.Equals(Id, Name, StringComparison.Ordinal)
            ? Id
            : $"{Name} [{Id}]";
    }
}
