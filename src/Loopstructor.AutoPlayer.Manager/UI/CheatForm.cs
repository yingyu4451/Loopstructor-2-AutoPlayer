using System.Globalization;
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

    private CheckBox _enableCheck = null!;
    private Label _versionLabel = null!;
    private Panel _statusBanner = null!;
    private Label _statusTitle = null!;
    private Label _statusDetail = null!;
    private TabControl _tabs = null!;

    private Button _catalogRefreshButton = null!;
    private Label _catalogSummary = null!;
    private ComboBox _vehicleCatalog = null!;
    private NumericUpDown _vehicleCount = null!;
    private CheckBox _enchantedCheck = null!;
    private ComboBox _enchantmentCatalog = null!;
    private NumericUpDown _enchantmentLevel = null!;
    private Button _grantVehicleButton = null!;
    private ComboBox _disposableCatalog = null!;
    private NumericUpDown _disposableCount = null!;
    private Button _grantDisposableButton = null!;
    private ComboBox _relicCatalog = null!;
    private Button _grantRelicButton = null!;

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

    private ComboBox _enemyCatalog = null!;
    private NumericUpDown _enemyLevel = null!;
    private NumericUpDown _enemyCount = null!;
    private NumericUpDown _spawnX = null!;
    private NumericUpDown _spawnY = null!;
    private NumericUpDown _spawnZ = null!;
    private Button _spawnEnemyButton = null!;

    public CheatForm(Func<string, JObject?, Task<ControlResponse?>> sendCommand)
    {
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));

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

        string pluginVersion = status?.PluginVersion ?? hello?.PluginVersion ?? string.Empty;
        _versionLabel.Text = $"{ManagerProductInfo.DisplayText}   /   插件 v{Display(pluginVersion)}   /   作弊协议 v{hello?.CheatProtocolVersion ?? Protocol.CheatCurrentVersion}";
        ApplyAvailability();
    }

    internal void SelectDemoTab(int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
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
            Text = "启用本次作弊会话",
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
        TableLayoutPanel layout = PageLayout(4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 29));

        FlowLayoutPanel toolbar = HorizontalFlow();
        _catalogRefreshButton = QuietButton("刷新资源目录", 120);
        _catalogRefreshButton.Click += async (_, _) => await RefreshCatalogAsync();
        _catalogSummary = InlineStatus("尚未读取资源目录", 520);
        toolbar.Controls.Add(_catalogRefreshButton);
        toolbar.Controls.Add(_catalogSummary);
        layout.Controls.Add(toolbar, 0, 0);

        _vehicleCatalog = CatalogCombo(250);
        _vehicleCount = IntegerInput(1, 20, 1, 82);
        _enchantedCheck = Toggle("附魔", 72);
        _enchantmentCatalog = CatalogCombo(210);
        _enchantmentLevel = IntegerInput(1, 7, 1, 82);
        _grantVehicleButton = Theme.CommandButton("获取战车", Theme.Teal, 108);
        _grantVehicleButton.Margin = new Padding(0, 23, 8, 0);
        _enchantedCheck.CheckedChanged += (_, _) => ApplyAvailability();
        _grantVehicleButton.Click += async (_, _) => await GrantVehicleAsync();
        FlowLayoutPanel vehicleControls = HorizontalFlow(true);
        vehicleControls.Controls.AddRange(new Control[]
        {
            Field("战车", _vehicleCatalog, 260),
            Field("数量", _vehicleCount, 92),
            Field("选项", _enchantedCheck, 82),
            Field("附魔", _enchantmentCatalog, 220),
            Field("附魔等级", _enchantmentLevel, 96),
            _grantVehicleButton
        });
        layout.Controls.Add(FlatSection("获取指定战车", vehicleControls), 0, 1);

        _disposableCatalog = CatalogCombo(280);
        _disposableCount = IntegerInput(1, 20, 1, 82);
        _grantDisposableButton = Theme.CommandButton("获取消耗品", Theme.Teal, 116);
        _grantDisposableButton.Margin = new Padding(0, 23, 8, 0);
        _grantDisposableButton.Click += async (_, _) => await GrantDisposableAsync();
        FlowLayoutPanel disposableControls = HorizontalFlow(true);
        disposableControls.Controls.AddRange(new Control[]
        {
            Field("消耗品", _disposableCatalog, 290),
            Field("数量", _disposableCount, 92),
            _grantDisposableButton
        });
        layout.Controls.Add(FlatSection("获取指定消耗品", disposableControls), 0, 2);

        _relicCatalog = CatalogCombo(280);
        _grantRelicButton = Theme.CommandButton("获取遗物", Theme.Teal, 108);
        _grantRelicButton.Margin = new Padding(0, 23, 8, 0);
        _grantRelicButton.Click += async (_, _) => await GrantRelicAsync();
        FlowLayoutPanel relicControls = HorizontalFlow(true);
        relicControls.Controls.AddRange(new Control[]
        {
            Field("遗物", _relicCatalog, 290),
            _grantRelicButton
        });
        layout.Controls.Add(FlatSection("获取指定遗物", relicControls, drawBottomLine: false), 0, 3);

        AddMutationControls(
            _vehicleCatalog, _vehicleCount, _enchantedCheck, _enchantmentCatalog, _enchantmentLevel,
            _grantVehicleButton, _disposableCatalog, _disposableCount, _grantDisposableButton,
            _relicCatalog, _grantRelicButton);
        _catalogQueryControls.Add(_catalogRefreshButton);
        page.Controls.Add(layout);
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

        _enemyCatalog = CatalogCombo(280);
        _enemyLevel = IntegerInput(1, 200, 1, 90);
        _enemyCount = IntegerInput(1, 10, 1, 82);
        _spawnX = CoordinateInput();
        _spawnY = CoordinateInput();
        _spawnZ = CoordinateInput();
        _spawnEnemyButton = Theme.CommandButton("生成怪物", Theme.Amber, 112);
        _spawnEnemyButton.Margin = new Padding(0, 23, 8, 0);
        _spawnEnemyButton.Click += async (_, _) => await SpawnEnemyAsync();
        FlowLayoutPanel controls = HorizontalFlow(true);
        controls.Padding = new Padding(0, 12, 0, 0);
        controls.Controls.AddRange(new Control[]
        {
            Field("怪物", _enemyCatalog, 290),
            Field("等级", _enemyLevel, 100),
            Field("数量", _enemyCount, 92),
            Field("X", _spawnX, 130),
            Field("Y", _spawnY, 130),
            Field("Z", _spawnZ, 130),
            _spawnEnemyButton
        });
        layout.Controls.Add(FlatSection("在指定世界坐标生成怪物", controls, drawBottomLine: false), 0, 1);

        _catalogQueryControls.Add(refresh);
        AddMutationControls(_enemyCatalog, _enemyLevel, _enemyCount, _spawnX, _spawnY, _spawnZ, _spawnEnemyButton);
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

        PopulateCatalog(_vehicleCatalog, response.Data["vehicles"] as JArray);
        PopulateCatalog(_enchantmentCatalog, response.Data["enchantments"] as JArray);
        PopulateCatalog(_disposableCatalog, response.Data["disposables"] as JArray);
        PopulateCatalog(_relicCatalog, response.Data["relics"] as JArray);
        PopulateCatalog(_enemyCatalog, response.Data["enemies"] as JArray);
        ApplyCatalogLimits(response.Data["limits"] as JObject);
        _catalogSummary.Text = $"战车 {_vehicleCatalog.Items.Count} / 附魔 {_enchantmentCatalog.Items.Count} / 消耗品 {_disposableCatalog.Items.Count} / 遗物 {_relicCatalog.Items.Count} / 怪物 {_enemyCatalog.Items.Count}";
    }

    private async Task GrantVehicleAsync()
    {
        if (!TryCatalogSelection(_vehicleCatalog, "请选择战车。", out CatalogItem? vehicle)) return;
        CatalogItem? enchantment = _enchantmentCatalog.SelectedItem as CatalogItem;
        if (_enchantedCheck.Checked && enchantment == null)
        {
            ShowLocalError("已选择附魔，请指定附魔类型。");
            return;
        }

        JObject arguments = new()
        {
            ["vehicleId"] = vehicle!.Id,
            ["count"] = Decimal.ToInt32(_vehicleCount.Value),
            ["enchanted"] = _enchantedCheck.Checked,
            ["enchantmentId"] = enchantment?.Id ?? string.Empty,
            ["enchantmentLevel"] = Decimal.ToInt32(_enchantmentLevel.Value)
        };
        await ExecuteCommandAsync(CheatCommands.GrantVehicle, arguments);
    }

    private async Task GrantDisposableAsync()
    {
        if (!TryCatalogSelection(_disposableCatalog, "请选择消耗品。", out CatalogItem? item)) return;
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
        if (!TryCatalogSelection(_relicCatalog, "请选择遗物。", out CatalogItem? item)) return;
        await ExecuteCommandAsync(
            CheatCommands.GrantRelic,
            new JObject { ["relicId"] = item!.Id });
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
        if (!TryCatalogSelection(_enemyCatalog, "请选择要生成的怪物。", out CatalogItem? enemy)) return;
        await ExecuteCommandAsync(
            CheatCommands.SpawnEnemy,
            new JObject
            {
                ["enemyId"] = enemy!.Id,
                ["level"] = Decimal.ToInt32(_enemyLevel.Value),
                ["count"] = Decimal.ToInt32(_enemyCount.Value),
                ["x"] = Decimal.ToDouble(_spawnX.Value),
                ["y"] = Decimal.ToDouble(_spawnY.Value),
                ["z"] = Decimal.ToDouble(_spawnZ.Value)
            });
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
            ShowLocalError("发送作弊命令失败：" + exception.Message);
            return null;
        }
        finally
        {
            _busy = false;
            ApplyAvailability();
        }
    }

    private void ApplyStateData(JObject? data)
    {
        if (data == null) return;
        if (data.Value<bool?>("outcomeUnknown") == true) _writeOutcomeUnknown = true;
        _synchronizing = true;
        try
        {
            bool? enabled = data.Value<bool?>("enabled");
            if (enabled.HasValue) _enableCheck.Checked = enabled.Value;
            bool? overlay = data.Value<bool?>("enemyIdsVisible") ?? data.Value<bool?>("visible");
            if (overlay.HasValue) _enemyIdOverlayCheck.Checked = overlay.Value;
            bool? godMode = data.Value<bool?>("baseGodMode")
                            ?? data.Value<bool?>("requested")
                            ?? data.Value<bool?>("actual");
            if (godMode.HasValue) _baseGodModeCheck.Checked = godMode.Value;
        }
        finally
        {
            _synchronizing = false;
        }
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
        if (_enchantmentCatalog != null)
        {
            _enchantmentCatalog.Enabled = canMutate && _enchantedCheck.Checked;
            _enchantmentLevel.Enabled = canMutate && _enchantedCheck.Checked;
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
                ? $"作弊模式已启用 / 已执行 {_status.CheatActionCount} 次修改"
                : "作弊模式已启用";
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

        if (_status?.CheatUsed == true) return "本轮测试已标记为使用作弊，不应作为自然自动游玩结果。";
        return "启用后可以执行资源、战斗、属性和怪物生成命令。";
    }

    private void ClearSessionData()
    {
        _lastMessage = string.Empty;
        _lastMessageIsError = false;
        _writeOutcomeUnknown = false;
        ClearCombo(_vehicleCatalog);
        ClearCombo(_enchantmentCatalog);
        ClearCombo(_disposableCatalog);
        ClearCombo(_relicCatalog);
        ClearCombo(_enemyCatalog);
        _vehicleGrid?.Rows.Clear();
        _enemyGrid?.Rows.Clear();
        if (_catalogSummary != null) _catalogSummary.Text = "尚未读取资源目录";
        if (_vehicleSummary != null) _vehicleSummary.Text = "尚未读取战车";
        if (_enemySummary != null) _enemySummary.Text = "尚未读取敌人";
        if (_baseGodModeCheck != null) SetCheckedSilently(_baseGodModeCheck, false);
        if (_enemyIdOverlayCheck != null) SetCheckedSilently(_enemyIdOverlayCheck, false);
    }

    private void PopulateCatalog(ComboBox combo, JArray? items)
    {
        string selectedId = (combo.SelectedItem as CatalogItem)?.Id ?? string.Empty;
        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            foreach (JObject item in items?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string id = item.Value<string>("id") ?? item["id"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;
                string name = item.Value<string>("name") ?? id;
                combo.Items.Add(new CatalogItem(id, name));
            }

            int matchingIndex = combo.Items.Cast<CatalogItem>()
                .Select((item, index) => new { item, index })
                .FirstOrDefault(pair => string.Equals(pair.item.Id, selectedId, StringComparison.Ordinal))
                ?.index ?? -1;
            combo.SelectedIndex = matchingIndex >= 0 ? matchingIndex : combo.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            combo.EndUpdate();
        }
    }

    private void ApplyCatalogLimits(JObject? limits)
    {
        if (limits == null) return;
        SetMaximum(_vehicleCount, limits.Value<int?>("maxGrantCount"));
        SetMaximum(_disposableCount, limits.Value<int?>("maxGrantCount"));
        SetMaximum(_enchantmentLevel, limits.Value<int?>("maxEnchantmentLevel"));
        SetMaximum(_enemyLevel, limits.Value<int?>("maxEnemyLevel"));
        SetMaximum(_enemyCount, limits.Value<int?>("maxSpawnCount"));
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

    private bool TryCatalogSelection(ComboBox combo, string error, out CatalogItem? item)
    {
        item = combo.SelectedItem as CatalogItem;
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

    private static void ClearCombo(ComboBox? combo)
    {
        if (combo == null) return;
        combo.Items.Clear();
        combo.SelectedIndex = -1;
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

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record CatalogItem(string Id, string Name)
    {
        public override string ToString() => string.Equals(Id, Name, StringComparison.Ordinal)
            ? Id
            : $"{Name} [{Id}]";
    }

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
