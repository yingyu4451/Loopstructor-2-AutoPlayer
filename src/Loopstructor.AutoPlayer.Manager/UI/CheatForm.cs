using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed partial class CheatForm : Window
{
    private const decimal DefaultAttributeMinimum = -1_000_000_000m;
    private const decimal DefaultAttributeMaximum = 1_000_000_000m;

    private static readonly SolidColorBrush BlueBrush = FrozenBrush(0x32, 0x8C, 0xC5);
    private static readonly SolidColorBrush RedBrush = FrozenBrush(0xD7, 0x4B, 0x31);
    private static readonly SolidColorBrush AmberBrush = FrozenBrush(0xF0, 0xB2, 0x3D);
    private static readonly SolidColorBrush GreenBrush = FrozenBrush(0x78, 0xD1, 0x3E);
    private static readonly SolidColorBrush MutedBrush = FrozenBrush(0xB9, 0xAA, 0x92);
    private static readonly SolidColorBrush BluePanelBrush = FrozenBrush(0x18, 0x2B, 0x36);
    private static readonly SolidColorBrush RedPanelBrush = FrozenBrush(0x30, 0x18, 0x14);
    private static readonly SolidColorBrush AmberPanelBrush = FrozenBrush(0x2D, 0x23, 0x13);
    private static readonly SolidColorBrush GreenPanelBrush = FrozenBrush(0x18, 0x29, 0x17);

    private readonly Func<string, JObject?, Task<ControlResponse?>> _sendCommand;
    private readonly List<UIElement> _mutationControls = new();
    private readonly List<UIElement> _catalogQueryControls = new();
    private readonly List<UIElement> _entityQueryControls = new();
    private readonly Dictionary<string, BitmapSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeCatalogIconKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _capturePollTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly ObservableCollection<CheatEnchantmentSelection> _enchantmentSelections = new();
    private readonly ObservableCollection<CheatVehicleRow> _vehicleRows = new();
    private readonly ObservableCollection<CheatEnemyRow> _enemyRows = new();
    private readonly ObservableCollection<CheatFieldCatapultRow> _fieldCatapultRows = new();
    private readonly ObservableCollection<CheatSpawnPointRow> _spawnPointRows = new();

    private bool _trusted;
    private bool _busy;
    private bool _synchronizing;
    private bool _loadingEntities;
    private bool _writeOutcomeUnknown;
    private bool _capturePollInProgress;
    private bool _isClosed;
    private bool _hasLoaded;
    private BridgeHello? _hello;
    private AutoPlayerStatus? _status;
    private string _sessionKey = string.Empty;
    private string _lastMessage = string.Empty;
    private bool _lastMessageIsError;
    private long _captureEpoch;
    private string _spawnCaptureState = "idle";
    private int _maxEnchantmentsPerVehicle = 5;
    private int? _lastResolvedEnemyLevel;
    private string _lastResolvedLevelSource = string.Empty;

    public CheatForm(Func<string, JObject?, Task<ControlResponse?>> sendCommand)
    {
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        InitializeComponent();

        Title = $"Loopstructor 2.AutoPlayer 作弊工具 - v{ManagerProductInfo.Version}";
        Shell.Subtitle = "CHEAT CONTROL CONSOLE";
        _versionLabel.Text = $"{ManagerProductInfo.DisplayText}   /   插件 v-   /   作弊协议 v{Protocol.CheatCurrentVersion}";
        _enchantmentGrid.ItemsSource = _enchantmentSelections;
        _vehicleGrid.ItemsSource = _vehicleRows;
        _enemyGrid.ItemsSource = _enemyRows;
        _fieldCatapultGrid.ItemsSource = _fieldCatapultRows;
        _spawnPointGrid.ItemsSource = _spawnPointRows;

        WireEvents();
        RegisterAvailabilityControls();
        Loaded += CheatForm_OnLoaded;
        Closing += CheatForm_OnClosing;
        Closed += CheatForm_OnClosed;
        _capturePollTimer.Tick += CapturePollTimerTick;
        ApplyAvailability();
    }

    public void UpdateSession(bool trusted, BridgeHello? hello, AutoPlayerStatus? status)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateSession(trusted, hello, status));
            return;
        }

        string nextSessionKey = BuildSessionKey(hello);
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
            _enableCheck.IsChecked = status?.CheatModeEnabled ?? hello?.CheatModeEnabled ?? false;
            _enemyIdOverlayCheck.IsChecked = status?.EnemyIdsVisible ?? false;
            _enemyBuffOverlayCheck.IsChecked = status?.EnemyBuffsVisible ?? false;
            _baseGodModeCheck.IsChecked = status?.BaseGodModeEnabled ?? false;
            _mapSkipCheck.IsChecked = status?.MapSkipEnabled ?? hello?.MapSkipEnabled ?? false;
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

    internal static string BuildSessionKey(BridgeHello? hello) => hello == null
        ? string.Empty
        : $"{hello.GameProcessId}|{hello.ProcessInstanceId}|{hello.BuildGuid}|{hello.AssemblySha256}|{hello.ArtifactRoot}";

    internal void SelectDemoTab(int index)
    {
        if (index >= 0 && index < _tabs.Items.Count) _tabs.SelectedIndex = index;
    }

    private void WireEvents()
    {
        _enableCheck.Click += async (_, _) => await EnableCheckChangedAsync();
        _catalogRefreshButton.Click += async (_, _) => await RefreshCatalogAsync();
        _spawnCatalogRefreshButton.Click += async (_, _) => await RefreshCatalogAsync();
        _grantVehicleButton.Click += async (_, _) => await GrantVehicleAsync();
        _addEnchantmentButton.Click += (_, _) => AddOrUpdateEnchantment();
        _removeEnchantmentButton.Click += (_, _) => RemoveSelectedEnchantment();
        _clearEnchantmentsButton.Click += (_, _) => ClearEnchantments();
        _grantDisposableButton.Click += async (_, _) => await GrantDisposableAsync();
        _grantRelicButton.Click += async (_, _) => await GrantRelicAsync();
        _removeRelicButton.Click += async (_, _) => await RemoveRelicAsync();
        _grantCatapultButton.Click += async (_, _) => await GrantCatapultPointAsync();
        _removeCatapultButton.Click += async (_, _) => await RemoveCatapultPointAsync();
        _baseGodModeCheck.Click += async (_, _) => await BaseGodModeChangedAsync();
        _mapSkipCheck.Click += async (_, _) => await MapSkipChangedAsync();
        _enemyIdOverlayCheck.Click += async (_, _) => await EnemyIdOverlayChangedAsync();
        _enemyBuffOverlayCheck.Click += async (_, _) => await EnemyBuffOverlayChangedAsync();
        _endWaveButton.Click += async (_, _) => await ConfirmAndExecuteAsync(
            "结束当前波次",
            "确定要立即结束当前波次吗？此操作会改变本轮测试结果。",
            CheatCommands.EndWave);
        _clearEnemiesButton.Click += async (_, _) => await ConfirmAndExecuteAsync(
            "清除所有敌人",
            "确定要清除当前场景中的所有敌人吗？此操作无法撤销。",
            CheatCommands.ClearEnemies);
        _vehicleRefreshButton.Click += async (_, _) => { await RefreshVehiclesAsync(); };
        _enemyRefreshButton.Click += async (_, _) => { await RefreshEnemiesAsync(); };
        _fieldCatapultRefreshButton.Click += async (_, _) => await RefreshFieldCatapultsAsync();
        _removeFieldCatapultButton.Click += async (_, _) => await RemoveFieldCatapultAsync();
        _clearFieldCatapultsButton.Click += async (_, _) => await ClearFieldCatapultsAsync();
        _removeVehicleButton.Click += async (_, _) => await RemoveVehicleAsync();
        _modifyVehicleButton.Click += async (_, _) => await ModifyVehicleAsync();
        _setVehicleEnchantmentButton.Click += async (_, _) => await SetVehicleEnchantmentAsync();
        _modifyEnemyButton.Click += async (_, _) => await ModifyEnemyAsync();
        _capturePointButton.Click += async (_, _) => await SetSpawnPointCaptureAsync(true);
        _cancelCaptureButton.Click += async (_, _) => await SetSpawnPointCaptureAsync(false);
        _addSpawnPointButton.Click += (_, _) => AddManualSpawnPoint();
        _removeSpawnPointButton.Click += async (_, _) => await RemoveSelectedSpawnPointAsync();
        _clearSpawnPointsButton.Click += async (_, _) => await ClearSpawnPointsAsync();
        _followCurrentLevelCheck.Click += (_, _) =>
        {
            _lastResolvedEnemyLevel = null;
            _lastResolvedLevelSource = string.Empty;
            ApplyAvailability();
        };
        _spawnEnemyButton.Click += async (_, _) => await SpawnEnemyAsync();
        _fieldCatapultGrid.SelectionChanged += (_, _) => ApplyAvailability();
        _spawnPointGrid.SelectionChanged += (_, _) => ApplyAvailability();

        foreach (CatalogPickerControl picker in new[]
                 {
                     _vehicleCatalog, _enchantmentCatalog, _disposableCatalog, _relicCatalog,
                     _ownedRelicCatalog, _catapultCatalog, _ownedCatapultCatalog, _enemyCatalog
                 })
        {
            picker.SelectedItemChanged += (_, _) => ApplyAvailability();
        }

        _vehicleAttribute.SelectedItemChanged += (_, _) =>
        {
            AttributeSelectionChanged(_vehicleAttribute, _vehicleCurrentValue, _vehicleAttributeValue);
            ApplyAvailability();
        };
        _enemyAttribute.SelectedItemChanged += (_, _) =>
        {
            AttributeSelectionChanged(_enemyAttribute, _enemyCurrentValue, _enemyAttributeValue);
            ApplyAvailability();
        };
        _vehicleEnchantmentCatalog.SelectedItemChanged += (_, _) =>
        {
            UpdateVehicleEnchantmentEditor(copyCurrentToInput: true);
            ApplyAvailability();
        };
    }

    private void RegisterAvailabilityControls()
    {
        AddMutationControls(
            _vehicleCatalog, _vehicleCount, _enchantmentCatalog, _enchantmentLevel,
            _addEnchantmentButton, _removeEnchantmentButton, _clearEnchantmentsButton,
            _grantVehicleButton, _disposableCatalog, _disposableCount, _grantDisposableButton,
            _relicCatalog, _ownedRelicCatalog, _grantRelicButton, _removeRelicButton,
            _catapultCatalog, _ownedCatapultCatalog, _catapultCount, _grantCatapultButton, _removeCatapultButton,
            _baseGodModeCheck, _mapSkipCheck, _endWaveButton, _clearEnemiesButton,
            _enemyIdOverlayCheck, _enemyBuffOverlayCheck,
            _removeFieldCatapultButton, _clearFieldCatapultsButton,
            _vehicleAttribute, _vehicleAttributeValue, _modifyVehicleButton,
            _vehicleEnchantmentCatalog, _vehicleEnchantmentLevel, _setVehicleEnchantmentButton, _removeVehicleButton,
            _enemyAttribute, _enemyAttributeValue, _modifyEnemyButton,
            _enemyCatalog, _enemyLevel, _followCurrentLevelCheck, _enemyCount, _spawnX, _spawnY, _spawnZ, _spawnRadius,
            _capturePointButton, _cancelCaptureButton, _addSpawnPointButton,
            _removeSpawnPointButton, _clearSpawnPointsButton, _spawnEnemyButton);
        _catalogQueryControls.AddRange(new UIElement[] { _catalogRefreshButton, _spawnCatalogRefreshButton });
        _entityQueryControls.AddRange(new UIElement[] { _vehicleRefreshButton, _enemyRefreshButton, _fieldCatapultRefreshButton });
    }

    private async void CheatForm_OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_hasLoaded) return;
        _hasLoaded = true;
        await RefreshStateOnShownAsync();
    }

    private void CheatForm_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_spawnCaptureState, "arming", StringComparison.OrdinalIgnoreCase))
        {
            _captureEpoch++;
            _capturePollTimer.Stop();
            _spawnCaptureState = "idle";
            _ = CancelSpawnPointCaptureOnCloseAsync();
        }
    }

    private void CheatForm_OnClosed(object? sender, EventArgs eventArgs)
    {
        _isClosed = true;
        _capturePollTimer.Stop();
        _capturePollTimer.Tick -= CapturePollTimerTick;
        _iconCache.Clear();
        _activeCatalogIconKeys.Clear();
    }

    private void EnchantmentGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) => ApplyAvailability();

    private void VehicleGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        EntitySelectionChanged(_vehicleGrid, _vehicleAttribute, _vehicleCurrentValue, _vehicleAttributeValue);
        UpdateVehicleEnchantmentEditor(copyCurrentToInput: true);
    }

    private void EnemyGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) =>
        EntitySelectionChanged(_enemyGrid, _enemyAttribute, _enemyCurrentValue, _enemyAttributeValue);

    private async Task EnableCheckChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _enableCheck.IsChecked == true;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetEnabled,
            new JObject { ["enabled"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_enableCheck, !requested);
            return;
        }

        ApplyStateData(response.Data);
        if (requested) await RefreshCatalogAsync(announce: false);
    }

    private async Task RefreshStateOnShownAsync()
    {
        if (!_trusted || _status?.CheatAvailable != true) return;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryState, null, announce: false);
        if (response?.Success == true)
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
        PopulateCatalog(_vehicleEnchantmentCatalog, response.Data["enchantments"] as JArray);
        PopulateCatalog(_disposableCatalog, response.Data["disposables"] as JArray);
        PopulateCatalog(_relicCatalog, response.Data["relics"] as JArray);
        PopulateCatalog(_enemyCatalog, response.Data["enemies"] as JArray);
        PopulateCatalog(_catapultCatalog, response.Data["catapultPoints"] as JArray);
        DisposeUnusedCatalogIcons();
        ApplyCatalogLimits(response.Data["limits"] as JObject);
        _catalogSummary.Text = $"战车 {_vehicleCatalog.ItemCount} / 附魔 {_enchantmentCatalog.ItemCount} / 消耗品 {_disposableCatalog.ItemCount} / 遗物 {_relicCatalog.ItemCount} / 弹射点 {_catapultCatalog.ItemCount} / 怪物 {_enemyCatalog.ItemCount}";
        await RefreshOwnedStateAsync();
    }

    private async Task GrantVehicleAsync()
    {
        if (!TryCatalogSelection(_vehicleCatalog, "请选择战车。", out CatalogPickerItem? vehicle)) return;
        JArray enchantments = new();
        foreach (CheatEnchantmentSelection selection in _enchantmentSelections)
        {
            enchantments.Add(new JObject
            {
                ["enchantmentId"] = selection.Id,
                ["level"] = selection.Level
            });
        }

        await ExecuteCommandAsync(
            CheatCommands.GrantVehicle,
            new JObject
            {
                ["vehicleId"] = vehicle!.Id,
                ["count"] = Decimal.ToInt32(_vehicleCount.Value),
                ["enchantments"] = enchantments
            });
    }

    private void AddOrUpdateEnchantment()
    {
        if (!TryCatalogSelection(_enchantmentCatalog, "请选择要添加的附魔。", out CatalogPickerItem? item)) return;
        int level = Decimal.ToInt32(_enchantmentLevel.Value);
        int existingIndex = -1;
        for (int index = 0; index < _enchantmentSelections.Count; index++)
        {
            if (string.Equals(_enchantmentSelections[index].Id, item!.Id, StringComparison.Ordinal))
            {
                existingIndex = index;
                break;
            }
        }

        CheatEnchantmentSelection next = new(item!, level);
        if (existingIndex >= 0)
        {
            _enchantmentSelections[existingIndex] = next;
            _enchantmentGrid.SelectedItem = next;
            _enchantmentGrid.ScrollIntoView(next);
            UpdateEnchantmentSummary("已更新附魔等级");
            return;
        }

        if (_enchantmentSelections.Count >= _maxEnchantmentsPerVehicle)
        {
            ShowLocalError($"一辆战车最多添加 {_maxEnchantmentsPerVehicle} 个附魔。");
            return;
        }

        _enchantmentSelections.Add(next);
        _enchantmentGrid.SelectedItem = next;
        _enchantmentGrid.ScrollIntoView(next);
        UpdateEnchantmentSummary("已添加附魔");
    }

    private void RemoveSelectedEnchantment()
    {
        if (_enchantmentGrid.SelectedItem is not CheatEnchantmentSelection selected) return;
        int index = _enchantmentSelections.IndexOf(selected);
        _enchantmentSelections.Remove(selected);
        if (_enchantmentSelections.Count > 0)
        {
            _enchantmentGrid.SelectedIndex = Math.Min(index, _enchantmentSelections.Count - 1);
        }
        UpdateEnchantmentSummary();
    }

    private void ClearEnchantments()
    {
        _enchantmentSelections.Clear();
        UpdateEnchantmentSummary();
    }

    private void UpdateEnchantmentSummary(string? action = null)
    {
        _enchantmentSummary.Text = string.IsNullOrWhiteSpace(action)
            ? $"已选 {_enchantmentSelections.Count} / {_maxEnchantmentsPerVehicle}"
            : $"{action} · {_enchantmentSelections.Count} / {_maxEnchantmentsPerVehicle}";
        _enchantmentSummary.Foreground = MutedBrush;
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
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.GrantRelic,
            new JObject { ["relicId"] = item!.Id });
        if (response?.Success == true) await RefreshOwnedStateAsync();
    }

    private async Task RemoveRelicAsync()
    {
        if (!TryCatalogSelection(_ownedRelicCatalog, "请从实际持有列表选择要删除的遗物。", out CatalogPickerItem? item)) return;
        JObject? owned = item!.Payload as JObject;
        string relicId = owned?.Value<string>("relicId") ?? item.EnumName;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.RemoveRelic,
            new JObject { ["relicId"] = relicId });
        if (response?.Success == true) await RefreshOwnedStateAsync();
    }

    private async Task GrantCatapultPointAsync()
    {
        if (!TryCatalogSelection(_catapultCatalog, "请选择弹射点。", out CatalogPickerItem? item)) return;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.GrantCatapultPoint,
            new JObject
            {
                ["disposableId"] = item!.Id,
                ["count"] = Decimal.ToInt32(_catapultCount.Value)
            });
        if (response?.Success == true) await RefreshOwnedStateAsync();
    }

    private async Task RemoveCatapultPointAsync()
    {
        if (!TryCatalogSelection(_ownedCatapultCatalog, "请从实际持有列表选择要删除的弹射点。", out CatalogPickerItem? item)) return;
        JObject? owned = item!.Payload as JObject;
        string disposableId = owned?.Value<string>("disposableId") ?? item.EnumName;
        int availableCount = Math.Max(1, owned?.Value<int?>("count") ?? 1);
        int requestedCount = Math.Min(Decimal.ToInt32(_catapultCount.Value), availableCount);
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.RemoveCatapultPoint,
            new JObject
            {
                ["disposableId"] = disposableId,
                ["catapultPointId"] = item.Id,
                ["count"] = requestedCount
            });
        if (response?.Success == true) await RefreshOwnedStateAsync();
    }

    private async Task RefreshOwnedStateAsync()
    {
        await ExecuteCommandAsync(CheatCommands.QueryState, null, announce: false);
    }

    private async Task RefreshFieldCatapultsAsync()
    {
        await ExecuteCommandAsync(CheatCommands.QueryState, null);
    }

    private async Task RemoveFieldCatapultAsync()
    {
        if (_fieldCatapultGrid.SelectedItem is not CheatFieldCatapultRow selected)
        {
            ShowLocalError("请选择要删除的场上弹射点。");
            return;
        }

        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.RemoveFieldCatapultPoint,
            new JObject { ["runtimeId"] = selected.RuntimeId });
        if (response?.Success == true && _fieldCatapultRows.Contains(selected))
        {
            _fieldCatapultRows.Remove(selected);
            UpdateFieldCatapultSummary();
        }
    }

    private async Task ClearFieldCatapultsAsync()
    {
        if (_fieldCatapultRows.Count == 0) return;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.ClearFieldCatapultPoints, null);
        if (response?.Success == true)
        {
            _fieldCatapultRows.Clear();
            UpdateFieldCatapultSummary();
        }
    }

    private async Task BaseGodModeChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _baseGodModeCheck.IsChecked == true;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetBaseGodMode,
            new JObject { ["enabled"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_baseGodModeCheck, !requested);
            return;
        }

        SetCheckedSilently(_baseGodModeCheck, response.Data?.Value<bool?>("requested") ?? requested);
    }

    private async Task MapSkipChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _mapSkipCheck.IsChecked == true;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetMapSkipEnabled,
            new JObject { ["enabled"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(_mapSkipCheck, !requested);
            return;
        }

        bool accepted = response.Data?.Value<bool?>("mapSkipEnabled")
                        ?? response.Data?.Value<bool?>("enabled")
                        ?? response.Data?.Value<bool?>("requested")
                        ?? requested;
        SetCheckedSilently(_mapSkipCheck, accepted);
    }

    private async Task EnemyIdOverlayChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _enemyIdOverlayCheck.IsChecked == true;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetEnemyIdOverlay,
            new JObject { ["visible"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(
                _enemyIdOverlayCheck,
                response?.Status?.EnemyIdsVisible
                ?? _status?.EnemyIdsVisible
                ?? !requested);
            return;
        }

        SetCheckedSilently(_enemyIdOverlayCheck, response.Data?.Value<bool?>("visible") ?? requested);
    }

    private async Task EnemyBuffOverlayChangedAsync()
    {
        if (_synchronizing) return;
        bool requested = _enemyBuffOverlayCheck.IsChecked == true;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetEnemyBuffOverlay,
            new JObject { ["visible"] = requested });
        if (response?.Success != true)
        {
            SetCheckedSilently(
                _enemyBuffOverlayCheck,
                response?.Status?.EnemyBuffsVisible
                ?? _status?.EnemyBuffsVisible
                ?? !requested);
            return;
        }

        SetCheckedSilently(
            _enemyBuffOverlayCheck,
            response.Data?.Value<bool?>("enemyBuffsVisible")
            ?? response.Data?.Value<bool?>("visible")
            ?? requested);
    }

    private async Task ConfirmAndExecuteAsync(string title, string prompt, string command)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            this,
            prompt,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        await ExecuteCommandAsync(command, new JObject());
    }

    private async Task<bool> RefreshVehiclesAsync(bool announce = true)
    {
        string previous = SelectedEntity(_vehicleGrid)?.Value<int?>("vehicleId")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryVehicles, null, announce);
        if (response?.Success != true) return false;
        PopulateVehicleGrid(response.Data?["vehicles"] as JArray, previous);
        return true;
    }

    private async Task<bool> RefreshEnemiesAsync(bool announce = true)
    {
        string previous = SelectedEntity(_enemyGrid)?.Value<string>("runtimeId") ?? string.Empty;
        ControlResponse? response = await ExecuteCommandAsync(CheatCommands.QueryEnemies, null, announce);
        if (response?.Success != true) return false;
        PopulateEnemyGrid(response.Data?["enemies"] as JArray, previous);
        return true;
    }

    private async Task RemoveVehicleAsync()
    {
        JObject? vehicle = SelectedEntity(_vehicleGrid);
        JToken? vehicleId = vehicle?["vehicleId"];
        if (vehicleId == null || vehicleId.Type == JTokenType.Null || string.IsNullOrWhiteSpace(vehicleId.ToString()))
        {
            ShowLocalError("请选择要删除的已有战车。");
            return;
        }

        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.RemoveVehicle,
            new JObject { ["vehicleId"] = vehicleId.DeepClone() });
        if (response?.Success == true)
        {
            string successMessage = _lastMessage;
            if (await RefreshVehiclesAsync(announce: false)) RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task ModifyVehicleAsync()
    {
        JObject? vehicle = SelectedEntity(_vehicleGrid);
        AttributeItem? attribute = _vehicleAttribute.SelectedCatalogItem?.Payload as AttributeItem;
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
            if (await RefreshVehiclesAsync(announce: false)) RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task SetVehicleEnchantmentAsync()
    {
        JObject? vehicle = SelectedEntity(_vehicleGrid);
        int? vehicleId = vehicle?.Value<int?>("vehicleId");
        if (!vehicleId.HasValue)
        {
            ShowLocalError("请选择要设置附魔的战车。");
            return;
        }
        if (!TryCatalogSelection(
                _vehicleEnchantmentCatalog,
                "请选择要设置的附魔。",
                out CatalogPickerItem? enchantment)) return;

        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetVehicleEnchantment,
            new JObject
            {
                ["vehicleId"] = vehicleId.Value,
                ["enchantmentId"] = enchantment!.Id,
                ["level"] = Decimal.ToInt32(_vehicleEnchantmentLevel.Value)
            });
        if (response?.Success == true)
        {
            string successMessage = _lastMessage;
            if (await RefreshVehiclesAsync(announce: false)) RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task ModifyEnemyAsync()
    {
        JObject? enemy = SelectedEntity(_enemyGrid);
        AttributeItem? attribute = _enemyAttribute.SelectedCatalogItem?.Payload as AttributeItem;
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
            if (await RefreshEnemiesAsync(announce: false)) RestoreSuccessfulMessage(successMessage);
        }
    }

    private async Task SpawnEnemyAsync()
    {
        if (!TryCatalogSelection(_enemyCatalog, "请选择要生成的怪物。", out CatalogPickerItem? enemy)) return;
        if (_spawnPointRows.Count == 0)
        {
            ShowLocalError("请先添加至少一个生成位置。");
            return;
        }

        int countPerPoint = Decimal.ToInt32(_enemyCount.Value);
        int requested = checked(countPerPoint * _spawnPointRows.Count);
        CheatSpawnPointRow firstPoint = _spawnPointRows[0];
        JArray points = new();
        JArray pointIds = new();
        foreach (CheatSpawnPointRow point in _spawnPointRows)
        {
            JObject position = new()
            {
                ["x"] = Decimal.ToDouble(point.XValue),
                ["y"] = Decimal.ToDouble(point.YValue),
                ["z"] = Decimal.ToDouble(point.ZValue)
            };
            if (!string.IsNullOrWhiteSpace(point.PointId))
            {
                position["pointId"] = point.PointId;
                pointIds.Add(point.PointId);
            }
            points.Add(position);
        }

        bool followCurrentLevel = _followCurrentLevelCheck.IsChecked == true;
        JObject payload = new()
        {
            ["enemyId"] = enemy!.Id,
            ["enumName"] = enemy.EnumName,
            ["levelMode"] = followCurrentLevel ? "current" : "custom",
            ["useCurrentLevel"] = followCurrentLevel,
            ["count"] = countPerPoint,
            ["points"] = points,
            ["pointIds"] = pointIds,
            ["x"] = Decimal.ToDouble(firstPoint.XValue),
            ["y"] = Decimal.ToDouble(firstPoint.YValue),
            ["z"] = Decimal.ToDouble(firstPoint.ZValue),
            ["position"] = new JObject
            {
                ["x"] = Decimal.ToDouble(firstPoint.XValue),
                ["y"] = Decimal.ToDouble(firstPoint.YValue),
                ["z"] = Decimal.ToDouble(firstPoint.ZValue)
            },
            ["spawnRadius"] = Decimal.ToDouble(_spawnRadius.Value)
        };
        if (!followCurrentLevel) payload["level"] = Decimal.ToInt32(_enemyLevel.Value);

        _spawnStatusLabel.Text = $"生成状态：正在 {points.Count} 个位置请求生成 {requested} 个怪物...";
        _spawnStatusLabel.Foreground = BlueBrush;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SpawnEnemy,
            payload);
        if (_isClosed) return;
        int accepted = response?.Data?.Value<int?>("requested") ?? requested;
        int spawned = response?.Data?.Value<int?>("spawned") ?? (response?.Success == true ? accepted : 0);
        _lastResolvedEnemyLevel = response?.Data?.Value<int?>("displayLevel")
                                  ?? response?.Data?.Value<int?>("resolvedLevel");
        _lastResolvedLevelSource = response?.Data?.Value<string>("levelSource") ?? string.Empty;
        UpdateResolvedLevelLabel();
        if (response?.Success == true && spawned >= accepted)
        {
            _spawnStatusLabel.Text = $"生成状态：已生成 {spawned} / {accepted}";
            _spawnStatusLabel.Foreground = GreenBrush;
        }
        else if (spawned > 0)
        {
            _spawnStatusLabel.Text = $"生成状态：部分成功，已生成 {spawned} / {accepted}";
            _spawnStatusLabel.Foreground = AmberBrush;
        }
        else
        {
            _spawnStatusLabel.Text = "生成状态：失败，未生成怪物";
            _spawnStatusLabel.Foreground = RedBrush;
        }
    }

    private void AddManualSpawnPoint()
    {
        AddSpawnPoint(string.Empty, _spawnX.Value, _spawnY.Value, _spawnZ.Value, "手工坐标");
        _spawnPointGrid.SelectedItem = _spawnPointRows.LastOrDefault();
        if (_spawnPointGrid.SelectedItem != null) _spawnPointGrid.ScrollIntoView(_spawnPointGrid.SelectedItem);
        RestoreSuccessfulMessage("已将手工坐标添加到生成位置列表。");
    }

    private async Task RemoveSelectedSpawnPointAsync()
    {
        if (_spawnPointGrid.SelectedItem is not CheatSpawnPointRow selected)
        {
            ShowLocalError("请选择要删除的生成位置。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(selected.PointId))
        {
            ControlResponse? response = await ExecuteCommandAsync(
                CheatCommands.RemoveSpawnPoint,
                new JObject { ["pointId"] = selected.PointId });
            if (response?.Success != true) return;
        }

        if (_spawnPointRows.Contains(selected)) _spawnPointRows.Remove(selected);
        RenumberSpawnPoints();
    }

    private async Task ClearSpawnPointsAsync()
    {
        bool hasCapturedPoints = _spawnPointRows.Any(point => !string.IsNullOrWhiteSpace(point.PointId));
        if (hasCapturedPoints)
        {
            ControlResponse? response = await ExecuteCommandAsync(CheatCommands.ClearSpawnPoints, null);
            if (response?.Success != true) return;
        }

        _spawnPointRows.Clear();
        UpdateSpawnPointSummary();
    }

    private void AddSpawnPoint(string pointId, decimal x, decimal y, decimal z, string source)
    {
        if (!string.IsNullOrWhiteSpace(pointId))
        {
            CheatSpawnPointRow? existing = _spawnPointRows.FirstOrDefault(point =>
                string.Equals(point.PointId, pointId, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.XValue = x;
                existing.YValue = y;
                existing.ZValue = z;
                existing.Source = source;
                _spawnPointGrid.Items.Refresh();
                return;
            }
        }

        _spawnPointRows.Add(new CheatSpawnPointRow
        {
            Number = _spawnPointRows.Count + 1,
            PointId = pointId,
            XValue = x,
            YValue = y,
            ZValue = z,
            Source = source
        });
        UpdateSpawnPointSummary();
    }

    private void RenumberSpawnPoints()
    {
        for (int index = 0; index < _spawnPointRows.Count; index++) _spawnPointRows[index].Number = index + 1;
        _spawnPointGrid.Items.Refresh();
        UpdateSpawnPointSummary();
    }

    private void UpdateSpawnPointSummary()
    {
        int captured = _spawnPointRows.Count(point => !string.IsNullOrWhiteSpace(point.PointId));
        int manual = _spawnPointRows.Count - captured;
        _spawnPointSummary.Text = _spawnPointRows.Count == 0
            ? "尚未添加生成位置"
            : $"共 {_spawnPointRows.Count} 个位置：游戏选点 {captured} / 手工 {manual}";
        ApplyAvailability();
    }

    private void UpdateResolvedLevelLabel()
    {
        bool followCurrentLevel = _followCurrentLevelCheck.IsChecked == true;
        string source = _lastResolvedLevelSource switch
        {
            "current-wave" => "当前波次 AI 等级",
            "custom" => "自定义",
            _ => followCurrentLevel ? "当前波次 AI 等级" : "自定义"
        };
        _resolvedLevelLabel.Text = _lastResolvedEnemyLevel.HasValue
            ? $"实际等级：{_lastResolvedEnemyLevel.Value}（{source}）"
            : followCurrentLevel
                ? "实际等级：跟随当前波次 AI 等级"
                : $"实际等级：自定义 {Decimal.ToInt32(_enemyLevel.Value)}";
    }

    private async Task SetSpawnPointCaptureAsync(bool enabled)
    {
        long operationEpoch = ++_captureEpoch;
        _spawnCaptureState = enabled ? "arming" : "cancelling";
        _captureStatusLabel.Text = enabled ? "选点状态：正在启动..." : "选点状态：正在取消...";
        _captureStatusLabel.Foreground = BlueBrush;
        ControlResponse? response = await ExecuteCommandAsync(
            CheatCommands.SetSpawnPointCapture,
            new JObject { ["enabled"] = enabled });
        if (_isClosed || operationEpoch != _captureEpoch) return;
        if (response?.Success != true)
        {
            _capturePollTimer.Stop();
            _spawnCaptureState = "failed";
            _captureStatusLabel.Text = enabled ? "选点状态：启动失败" : "选点状态：取消失败";
            _captureStatusLabel.Foreground = RedBrush;
            ApplyAvailability();
            return;
        }

        JObject? capture = ExtractSpawnPointCapture(response.Data);
        ApplySpawnPointCapture(capture ?? new JObject
        {
            ["state"] = enabled ? "armed" : "cancelled",
            ["message"] = enabled ? "等待游戏内选点" : "已取消选点"
        });
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
            // The plugin expires an abandoned capture request as a second line of cleanup.
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
            if (_isClosed
                || pollEpoch != _captureEpoch
                || !string.Equals(_sessionKey, pollSessionKey, StringComparison.Ordinal)) return;
            if (response == null)
            {
                SetCapturePollingError("插件没有返回选点状态。");
                return;
            }

            if (response.Status != null) UpdateSession(_trusted, response.Hello ?? _hello, response.Status);
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
            if (!_isClosed
                && pollEpoch == _captureEpoch
                && string.Equals(_sessionKey, pollSessionKey, StringComparison.Ordinal))
            {
                SetCapturePollingError("读取选点状态失败：" + exception.Message);
            }
        }
        finally
        {
            _capturePollInProgress = false;
            if (!_isClosed) ApplyAvailability();
        }
    }

    private void SetCapturePollingError(string message)
    {
        _capturePollTimer.Stop();
        _spawnCaptureState = "armed";
        _captureStatusLabel.Text = "选点状态：" + message;
        _captureStatusLabel.Foreground = RedBrush;
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
        if (capture["points"] is JArray capturedPoints) SyncCapturedSpawnPoints(capturedPoints);
        _spawnCaptureState = state;
        switch (state)
        {
            case "armed":
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：等待游戏内左 Alt + 左键..."
                    : "选点状态：" + message;
                _captureStatusLabel.Foreground = AmberBrush;
                if (!_capturePollTimer.IsEnabled) _capturePollTimer.Start();
                break;
            case "captured":
                _capturePollTimer.Stop();
                SetCoordinateValue(_spawnX, ToDecimal(capture["x"], _spawnX.Value));
                SetCoordinateValue(_spawnY, ToDecimal(capture["y"], _spawnY.Value));
                SetCoordinateValue(_spawnZ, ToDecimal(capture["z"], _spawnZ.Value));
                if (capture["points"] is not JArray)
                {
                    AddSpawnPoint(
                        capture.Value<string>("pointId") ?? string.Empty,
                        _spawnX.Value,
                        _spawnY.Value,
                        _spawnZ.Value,
                        "游戏选点");
                }
                _captureStatusLabel.Text = $"选点状态：已捕获 {FormatNumber(_spawnX.Value)} / {FormatNumber(_spawnY.Value)} / {FormatNumber(_spawnZ.Value)}";
                _captureStatusLabel.Foreground = GreenBrush;
                break;
            case "failed":
            case "expired":
                _capturePollTimer.Stop();
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：捕获失败"
                    : "选点状态：" + message;
                _captureStatusLabel.Foreground = RedBrush;
                break;
            case "cancelled":
            case "disabled":
            case "idle":
            default:
                _capturePollTimer.Stop();
                _captureStatusLabel.Text = string.IsNullOrWhiteSpace(message)
                    ? "选点状态：未启动"
                    : "选点状态：" + message;
                _captureStatusLabel.Foreground = MutedBrush;
                break;
        }

        ApplyAvailability();
    }

    private void SyncCapturedSpawnPoints(JArray points)
    {
        HashSet<string> returnedIds = new(StringComparer.Ordinal);
        foreach (JObject point in points.OfType<JObject>())
        {
            string pointId = point.Value<string>("pointId") ?? point.Value<string>("id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pointId)) continue;
            returnedIds.Add(pointId);
            AddSpawnPoint(
                pointId,
                ToDecimal(point["x"], 0),
                ToDecimal(point["y"], 0),
                ToDecimal(point["z"], 0),
                "游戏选点");
        }

        foreach (CheatSpawnPointRow stale in _spawnPointRows
                     .Where(point => !string.IsNullOrWhiteSpace(point.PointId) && !returnedIds.Contains(point.PointId))
                     .ToArray())
        {
            _spawnPointRows.Remove(stale);
        }
        RenumberSpawnPoints();
    }

    private void ResetSpawnPointCapture(string statusText)
    {
        _captureEpoch++;
        _capturePollTimer.Stop();
        _spawnCaptureState = "idle";
        _captureStatusLabel.Text = statusText;
        _captureStatusLabel.Foreground = MutedBrush;
    }

    private static void SetCoordinateValue(CheatNumericInput input, decimal value) =>
        input.Value = Math.Clamp(value, input.Minimum, input.Maximum);

    private async Task<ControlResponse?> ExecuteCommandAsync(
        string command,
        JObject? arguments,
        bool announce = true)
    {
        if (_isClosed || _busy) return null;
        string previousMessage = _lastMessage;
        bool previousMessageIsError = _lastMessageIsError;
        _busy = true;
        _lastMessage = "正在执行命令...";
        _lastMessageIsError = false;
        ApplyAvailability();
        try
        {
            ControlResponse? response = await _sendCommand(command, arguments);
            if (_isClosed) return null;
            if (response == null)
            {
                ShowLocalError("插件没有返回有效响应。");
                return null;
            }

            if (response.Status != null) UpdateSession(_trusted, response.Hello ?? _hello, response.Status);
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
            if (!_isClosed) ShowLocalError("发送作弊命令失败：" + exception.Message);
            return null;
        }
        finally
        {
            _busy = false;
            if (!_isClosed) ApplyAvailability();
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
            if (enabled.HasValue) _enableCheck.IsChecked = enabled.Value;
            bool? overlay = ReadBoolean(data["enemyIdsVisible"]);
            if (overlay.HasValue) _enemyIdOverlayCheck.IsChecked = overlay.Value;
            bool? buffOverlay = ReadBoolean(data["enemyBuffsVisible"]);
            if (buffOverlay.HasValue) _enemyBuffOverlayCheck.IsChecked = buffOverlay.Value;
            bool? godMode = ReadBoolean(data["baseGodMode"]);
            if (godMode.HasValue) _baseGodModeCheck.IsChecked = godMode.Value;
            bool? mapSkip = ReadBoolean(data["mapSkipEnabled"]) ?? ReadBoolean(data["mapSkipRequested"]);
            if (mapSkip.HasValue) _mapSkipCheck.IsChecked = mapSkip.Value;
        }
        finally
        {
            _synchronizing = false;
        }

        JObject? capture = ExtractSpawnPointCapture(data);
        if (capture != null) ApplySpawnPointCapture(capture);
        if (data["ownedRelics"] is JArray ownedRelics)
            PopulateOwnedCatalog(_ownedRelicCatalog, ownedRelics, "relicId", "遗物");
        if (data["ownedCatapultPoints"] is JArray ownedCatapultPoints)
            PopulateOwnedCatalog(_ownedCatapultCatalog, ownedCatapultPoints, "catapultPointId", "弹射点");
        if (data["fieldCatapultPoints"] is JArray fieldCatapults) PopulateFieldCatapultGrid(fieldCatapults);
    }

    private void ApplyAvailability()
    {
        if (_isClosed) return;
        bool available = _trusted && _status?.CheatAvailable == true;
        bool runConflict = _status?.RunState is AutoPlayerRunState.Running or AutoPlayerRunState.Paused;
        bool canMutate = available
                         && _status?.CheatModeEnabled == true
                         && !_writeOutcomeUnknown
                         && !_busy;
        bool canQueryCatalog = available && !_busy;
        bool canQueryEntities = available && _status?.CheatModeEnabled == true && !_busy;

        _enableCheck.IsEnabled = available && !runConflict && !_writeOutcomeUnknown && !_busy;
        foreach (UIElement control in _mutationControls) control.IsEnabled = canMutate;
        foreach (UIElement control in _catalogQueryControls) control.IsEnabled = canQueryCatalog;
        foreach (UIElement control in _entityQueryControls) control.IsEnabled = canQueryEntities;

        bool selectedAlready = _enchantmentCatalog.SelectedCatalogItem != null
                               && _enchantmentSelections.Any(selection => string.Equals(
                                   selection.Id,
                                   _enchantmentCatalog.SelectedCatalogItem.Id,
                                   StringComparison.Ordinal));
        _addEnchantmentButton.IsEnabled = canMutate
                                          && _enchantmentCatalog.SelectedCatalogItem != null
                                          && (selectedAlready || _enchantmentSelections.Count < _maxEnchantmentsPerVehicle);
        _removeEnchantmentButton.IsEnabled = canMutate && _enchantmentGrid.SelectedItem != null;
        _clearEnchantmentsButton.IsEnabled = canMutate && _enchantmentSelections.Count > 0;
        _grantVehicleButton.IsEnabled = canMutate && _vehicleCatalog.SelectedCatalogItem != null;
        _grantDisposableButton.IsEnabled = canMutate && _disposableCatalog.SelectedCatalogItem != null;
        _grantRelicButton.IsEnabled = canMutate && _relicCatalog.SelectedCatalogItem != null;
        _removeRelicButton.IsEnabled = canMutate && _ownedRelicCatalog.SelectedCatalogItem != null;
        _grantCatapultButton.IsEnabled = canMutate && _catapultCatalog.SelectedCatalogItem != null;
        _removeCatapultButton.IsEnabled = canMutate && _ownedCatapultCatalog.SelectedCatalogItem != null;
        _removeFieldCatapultButton.IsEnabled = canMutate && _fieldCatapultGrid.SelectedItem is CheatFieldCatapultRow;
        _clearFieldCatapultsButton.IsEnabled = canMutate && _fieldCatapultRows.Count > 0;

        bool captureArmed = string.Equals(_spawnCaptureState, "armed", StringComparison.OrdinalIgnoreCase);
        _capturePointButton.IsEnabled = canMutate && !captureArmed;
        _cancelCaptureButton.IsEnabled = canMutate && captureArmed;
        _enemyLevel.IsEnabled = canMutate && _followCurrentLevelCheck.IsChecked != true;
        UpdateResolvedLevelLabel();
        _removeSpawnPointButton.IsEnabled = canMutate && _spawnPointGrid.SelectedItem is CheatSpawnPointRow;
        _clearSpawnPointsButton.IsEnabled = canMutate && _spawnPointRows.Count > 0;
        _spawnEnemyButton.IsEnabled = canMutate
                                      && _enemyCatalog.SelectedCatalogItem != null
                                      && _spawnPointRows.Count > 0;

        _modifyVehicleButton.IsEnabled = canMutate
                                         && SelectedEntity(_vehicleGrid) != null
                                         && _vehicleAttribute.SelectedCatalogItem?.Payload is AttributeItem;
        _setVehicleEnchantmentButton.IsEnabled = canMutate
                                                  && SelectedEntity(_vehicleGrid) != null
                                                  && _vehicleEnchantmentCatalog.SelectedCatalogItem != null;
        _removeVehicleButton.IsEnabled = canMutate && SelectedEntity(_vehicleGrid) != null;
        _modifyEnemyButton.IsEnabled = canMutate
                                       && SelectedEntity(_enemyGrid) != null
                                       && _enemyAttribute.SelectedCatalogItem?.Payload is AttributeItem;

        RenderStatusBanner(available, runConflict);
    }

    private void RenderStatusBanner(bool available, bool runConflict)
    {
        Brush rail;
        Brush panel;
        if (_busy)
        {
            _statusTitle.Text = "正在执行作弊命令";
            rail = BlueBrush;
            panel = BluePanelBrush;
        }
        else if (_writeOutcomeUnknown)
        {
            _statusTitle.Text = "写命令结果未知 / 已冻结后续修改";
            rail = RedBrush;
            panel = RedPanelBrush;
        }
        else if (!_trusted)
        {
            _statusTitle.Text = "未连接到可信游戏会话";
            rail = RedBrush;
            panel = RedPanelBrush;
        }
        else if (!available)
        {
            _statusTitle.Text = "当前构建不支持作弊工具";
            rail = RedBrush;
            panel = RedPanelBrush;
        }
        else if (runConflict)
        {
            _statusTitle.Text = "自动游玩运行或暂停时不能启用作弊";
            rail = AmberBrush;
            panel = AmberPanelBrush;
        }
        else if (_status?.CheatModeEnabled == true)
        {
            _statusTitle.Text = _status.CheatUsed
                ? _status.CheatActionCount > 0
                    ? $"作弊模式已启用 / 本进程已尝试 {_status.CheatActionCount} 次修改"
                    : "作弊模式已启用 / 当前存档有历史作弊标记"
                : "作弊模式已启用";
            rail = AmberBrush;
            panel = AmberPanelBrush;
        }
        else if (_status?.CheatUsed == true)
        {
            _statusTitle.Text = "当前存档已被作弊修改 / 关闭作弊后仍可自动游玩";
            rail = AmberBrush;
            panel = AmberPanelBrush;
        }
        else
        {
            _statusTitle.Text = "作弊功能可用 / 尚未启用";
            rail = GreenBrush;
            panel = GreenPanelBrush;
        }

        if (_lastMessageIsError)
        {
            rail = RedBrush;
            panel = RedPanelBrush;
        }

        _statusTitle.Foreground = rail;
        _statusRail.Background = rail;
        _statusBanner.Background = panel;
        _statusDetail.Text = BuildStatusDetail(available);
    }

    private string BuildStatusDetail(bool available)
    {
        if (!string.IsNullOrWhiteSpace(_lastMessage)) return _lastMessage;
        if (!_trusted) return "请先启动已安装插件的游戏，并等待 Manager 完成安全握手。";
        if (!available)
        {
            string reason = _status?.CheatAvailabilityReason ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reason)) reason = _hello?.CheatAvailabilityReason ?? string.Empty;
            return string.IsNullOrWhiteSpace(reason) ? "插件未提供作弊运行时合同。" : reason;
        }

        if (_status?.CheatUsed == true)
        {
            return "当前配置已有作弊记录；关闭作弊模式后仍可自动游玩，结果会继续标记为 cheat-modified。";
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
        _vehicleCatalog.ClearItems();
        _enchantmentCatalog.ClearItems();
        _vehicleEnchantmentCatalog.ClearItems();
        _disposableCatalog.ClearItems();
        _relicCatalog.ClearItems();
        _ownedRelicCatalog.ClearItems();
        _enemyCatalog.ClearItems();
        _catapultCatalog.ClearItems();
        _ownedCatapultCatalog.ClearItems();
        _enchantmentSelections.Clear();
        _vehicleRows.Clear();
        _enemyRows.Clear();
        _fieldCatapultRows.Clear();
        _spawnPointRows.Clear();
        _iconCache.Clear();
        _activeCatalogIconKeys.Clear();
        _maxEnchantmentsPerVehicle = 5;
        _catalogSummary.Text = "尚未读取资源目录";
        _enchantmentSummary.Text = "已选 0 / 5";
        _vehicleSummary.Text = "尚未读取战车";
        _enemySummary.Text = "尚未读取敌人";
        _fieldCatapultSummary.Text = "尚未读取场上弹射点";
        _spawnPointSummary.Text = "尚未添加生成位置";
        _vehicleEnchantmentCurrent.Text = "当前等级 -";
        _lastResolvedEnemyLevel = null;
        _lastResolvedLevelSource = string.Empty;
        _followCurrentLevelCheck.IsChecked = true;
        UpdateResolvedLevelLabel();
        _captureStatusLabel.Text = "选点状态：未启动";
        _captureStatusLabel.Foreground = MutedBrush;
        _spawnStatusLabel.Text = "生成状态：尚未执行";
        _spawnStatusLabel.Foreground = MutedBrush;
        SetCheckedSilently(_baseGodModeCheck, false);
        SetCheckedSilently(_mapSkipCheck, false);
        SetCheckedSilently(_enemyIdOverlayCheck, false);
        SetCheckedSilently(_enemyBuffOverlayCheck, false);
    }

    private void PopulateCatalog(CatalogPickerControl picker, JArray? items)
    {
        List<CatalogPickerItem> catalogItems = new();
        foreach (JObject item in items?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string id = item.Value<string>("id") ?? item["id"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) continue;
            string enumName = item.Value<string>("enumName") ?? id;
            string name = item.Value<string>("name") ?? string.Empty;
            string fallbackName = item.Value<string>("fallbackName") ?? string.Empty;
            string iconFile = item.Value<string>("iconFile") ?? string.Empty;
            string iconSha256 = item.Value<string>("iconSha256") ?? string.Empty;
            string iconBase64 = item.Value<string>("iconBase64") ?? string.Empty;
            List<string> tags = (item["tags"] as JArray)?
                .Values<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList() ?? new List<string>();
            if (!tags.Contains(enumName, StringComparer.OrdinalIgnoreCase)) tags.Add(enumName);
            catalogItems.Add(new CatalogPickerItem(
                id,
                name,
                fallbackName,
                TryLoadCatalogIconBase64(iconBase64) ?? TryLoadCatalogIcon(iconFile, iconSha256),
                tags,
                item,
                enumName));
        }

        picker.SetItems(catalogItems);
        ApplyAvailability();
    }

    private void PopulateOwnedCatalog(
        CatalogPickerControl picker,
        JArray items,
        string identityProperty,
        string category)
    {
        List<CatalogPickerItem> catalogItems = new();
        foreach (JObject item in items.OfType<JObject>())
        {
            string stableId = item.Value<string>(identityProperty) ?? string.Empty;
            int count = Math.Max(0, item.Value<int?>("count") ?? 0);
            if (string.IsNullOrWhiteSpace(stableId) || count == 0) continue;

            string enumName = item.Value<string>("enumName")
                              ?? item.Value<string>("disposableId")
                              ?? item.Value<string>("relicId")
                              ?? stableId;
            string baseName = item.Value<string>("name")
                              ?? item.Value<string>("fallbackName")
                              ?? enumName;
            JArray buffs = item["buffs"] as JArray ?? new JArray();
            string detail = buffs.Count > 0
                ? $"持有 {count} · Buff {buffs.Count}"
                : $"持有 {count}";
            List<string> tags = new() { category, stableId, enumName, baseName };
            foreach (string? buff in buffs.Values<string>())
            {
                if (!string.IsNullOrWhiteSpace(buff)) tags.Add(buff);
            }

            catalogItems.Add(new CatalogPickerItem(
                stableId,
                $"{baseName} · {detail}",
                enumName,
                TryLoadCatalogIconBase64(item.Value<string>("iconBase64") ?? string.Empty)
                ?? TryLoadCatalogIcon(
                    item.Value<string>("iconFile") ?? string.Empty,
                    item.Value<string>("iconSha256") ?? string.Empty),
                tags,
                item,
                enumName));
        }

        picker.SetItems(catalogItems);
        ApplyAvailability();
    }

    private BitmapSource? TryLoadCatalogIconBase64(string iconBase64)
    {
        if (string.IsNullOrWhiteSpace(iconBase64) || iconBase64.Length > 2 * 1024 * 1024) return null;
        string encoded = iconBase64.Trim();
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int separator = encoded.IndexOf(',');
            if (separator <= 0 || !encoded[..separator].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            encoded = encoded[(separator + 1)..];
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0 || bytes.Length > 1024 * 1024) return null;
            string cacheKey = "inline|" + Convert.ToHexString(SHA256.HashData(bytes));
            _activeCatalogIconKeys.Add(cacheKey);
            if (_iconCache.TryGetValue(cacheKey, out BitmapSource? cached)) return cached;
            BitmapSource? image = DecodeCatalogIcon(bytes);
            if (image != null) _iconCache[cacheKey] = image;
            return image;
        }
        catch (Exception exception) when (exception is FormatException
                                          or ArgumentException
                                          or NotSupportedException
                                          or FileFormatException)
        {
            return null;
        }
    }

    private BitmapSource? TryLoadCatalogIcon(string iconFile, string iconSha256)
    {
        if (string.IsNullOrWhiteSpace(iconFile) || string.IsNullOrWhiteSpace(iconSha256)) return null;
        string artifactRoot = _hello?.ArtifactRoot ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artifactRoot)
            || !Path.IsPathFullyQualified(artifactRoot)
            || Path.IsPathRooted(iconFile)) return null;

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactRoot));
            string candidate = Path.GetFullPath(Path.Combine(root, iconFile));
            string relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, ".", StringComparison.Ordinal)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return null;

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
            if (directory == null || iconSha256.Length != 64) return null;

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
            if (_iconCache.TryGetValue(cacheKey, out BitmapSource? cached)) return cached;

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
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(bytes))) return null;

            BitmapSource? image = DecodeCatalogIcon(bytes);
            if (image != null) _iconCache[cacheKey] = image;
            return image;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or System.Security.SecurityException
                                          or FileFormatException)
        {
            return null;
        }
    }

    private static BitmapSource? DecodeCatalogIcon(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame? frame = decoder.Frames.FirstOrDefault();
        if (frame == null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            || frame.PixelWidth > 1024 || frame.PixelHeight > 1024) return null;
        if (frame.CanFreeze) frame.Freeze();
        return frame;
    }

    private void DisposeUnusedCatalogIcons()
    {
        foreach (string key in _iconCache.Keys.Where(key => !_activeCatalogIconKeys.Contains(key)).ToArray())
        {
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
        SetMaximum(_vehicleEnchantmentLevel, limits.Value<int?>("maxEnchantmentLevel"));
        SetMaximum(_enemyLevel, limits.Value<int?>("maxEnemyLevel"));
        SetMaximum(_enemyCount, limits.Value<int?>("maxSpawnCount"));
        SetMaximum(_spawnRadius, limits.Value<int?>("maxSpawnRadius"));
        _maxEnchantmentsPerVehicle = Math.Clamp(limits.Value<int?>("maxEnchantmentsPerVehicle") ?? 5, 0, 64);
        while (_enchantmentSelections.Count > _maxEnchantmentsPerVehicle)
        {
            _enchantmentSelections.RemoveAt(_enchantmentSelections.Count - 1);
        }
        UpdateEnchantmentSummary();
        decimal coordinateMaximum = Math.Max(1m, Math.Abs(ToDecimal(limits["maxCoordinateMagnitude"], 10_000m)));
        foreach (CheatNumericInput input in new[] { _spawnX, _spawnY, _spawnZ })
        {
            input.Minimum = -coordinateMaximum;
            input.Maximum = coordinateMaximum;
        }
    }

    private void PopulateVehicleGrid(JArray? vehicles, string selectedVehicleId)
    {
        _loadingEntities = true;
        try
        {
            _vehicleRows.Clear();
            foreach (JObject vehicle in vehicles?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string vehicleId = vehicle["vehicleId"]?.ToString() ?? string.Empty;
                string typeId = vehicle.Value<string>("typeId") ?? vehicle.Value<string>("enumName") ?? string.Empty;
                CatalogPickerItem? catalog = _vehicleCatalog.FindItem(typeId);
                string enumName = vehicle.Value<string>("enumName") ?? catalog?.EnumName ?? typeId;
                string name = vehicle.Value<string>("name") ?? catalog?.DisplayName ?? enumName;
                _vehicleRows.Add(new CheatVehicleRow(
                    string.IsNullOrWhiteSpace(vehicleId) ? "-" : vehicleId,
                    Display(enumName),
                    Display(name),
                    vehicle.Value<int?>("level")?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    ResolveItemIcon(vehicle, catalog),
                    BuildVehicleEnchantments(vehicle["enchantments"] as JArray),
                    FormatPosition(vehicle["position"] as JObject),
                    vehicle));
            }

            CheatVehicleRow? selected = _vehicleRows.FirstOrDefault(row =>
                string.Equals(row.VehicleId, selectedVehicleId, StringComparison.Ordinal))
                ?? _vehicleRows.FirstOrDefault();
            _vehicleGrid.SelectedItem = selected;
            if (selected != null) _vehicleGrid.ScrollIntoView(selected);
            _vehicleSummary.Text = $"共 {_vehicleRows.Count} 辆；修改命令使用战车 ID";
        }
        finally
        {
            _loadingEntities = false;
        }

        EntitySelectionChanged(_vehicleGrid, _vehicleAttribute, _vehicleCurrentValue, _vehicleAttributeValue);
        UpdateVehicleEnchantmentEditor(copyCurrentToInput: true);
    }

    private void PopulateEnemyGrid(JArray? enemies, string selectedRuntimeId)
    {
        _loadingEntities = true;
        try
        {
            _enemyRows.Clear();
            foreach (JObject enemy in enemies?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string typeId = enemy.Value<string>("typeId") ?? enemy.Value<string>("enumName") ?? string.Empty;
                CatalogPickerItem? catalog = _enemyCatalog.FindItem(typeId);
                string enumName = enemy.Value<string>("enumName") ?? catalog?.EnumName ?? typeId;
                string name = enemy.Value<string>("name") ?? catalog?.DisplayName ?? enumName;
                _enemyRows.Add(new CheatEnemyRow(
                    enemy.Value<string>("runtimeId") ?? string.Empty,
                    Display(enumName),
                    Display(name),
                    ResolveItemIcon(enemy, catalog),
                    FormatHealth(enemy["health"], enemy["healthMax"]),
                    FormatPosition(enemy["position"] as JObject),
                    enemy));
            }

            CheatEnemyRow? selected = _enemyRows.FirstOrDefault(row =>
                string.Equals(row.RuntimeId, selectedRuntimeId, StringComparison.Ordinal))
                ?? _enemyRows.FirstOrDefault();
            _enemyGrid.SelectedItem = selected;
            if (selected != null) _enemyGrid.ScrollIntoView(selected);
            _enemySummary.Text = $"共 {_enemyRows.Count} 个；修改命令使用运行时 ID";
        }
        finally
        {
            _loadingEntities = false;
        }

        EntitySelectionChanged(_enemyGrid, _enemyAttribute, _enemyCurrentValue, _enemyAttributeValue);
    }

    private IReadOnlyList<CheatEntityEnchantmentRow> BuildVehicleEnchantments(JArray? enchantments)
    {
        List<CheatEntityEnchantmentRow> result = new();
        foreach (JObject enchantment in enchantments?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string id = enchantment.Value<string>("id")
                        ?? enchantment.Value<string>("enchantmentId")
                        ?? enchantment.Value<string>("enumName")
                        ?? string.Empty;
            CatalogPickerItem? catalog = _vehicleEnchantmentCatalog.FindItem(id);
            string enumName = enchantment.Value<string>("enumName") ?? catalog?.EnumName ?? id;
            string name = enchantment.Value<string>("name") ?? catalog?.DisplayName ?? enumName;
            int level = Math.Max(0, enchantment.Value<int?>("level") ?? 0);
            result.Add(new CheatEntityEnchantmentRow(
                ResolveItemIcon(enchantment, catalog),
                $"{Display(name)} / 枚举 {Display(enumName)} / ID {Display(id)} / Lv.{level}",
                level.ToString(CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private ImageSource? ResolveItemIcon(JObject item, CatalogPickerItem? catalog) =>
        TryLoadCatalogIconBase64(item.Value<string>("iconBase64") ?? string.Empty)
        ?? TryLoadCatalogIcon(
            item.Value<string>("iconFile") ?? string.Empty,
            item.Value<string>("iconSha256") ?? string.Empty)
        ?? catalog?.Icon;

    private void PopulateFieldCatapultGrid(JArray points)
    {
        string selectedRuntimeId = (_fieldCatapultGrid.SelectedItem as CheatFieldCatapultRow)?.RuntimeId ?? string.Empty;
        _fieldCatapultRows.Clear();
        foreach (JObject point in points.OfType<JObject>())
        {
            string runtimeId = point.Value<string>("runtimeId") ?? point.Value<string>("id") ?? string.Empty;
            string typeId = point.Value<string>("disposableId")
                            ?? point.Value<string>("typeId")
                            ?? point.Value<string>("enumName")
                            ?? string.Empty;
            CatalogPickerItem? catalog = _catapultCatalog.FindItem(typeId);
            string enumName = point.Value<string>("enumName") ?? catalog?.EnumName ?? typeId;
            string name = point.Value<string>("name") ?? catalog?.DisplayName ?? enumName;
            _fieldCatapultRows.Add(new CheatFieldCatapultRow(
                runtimeId,
                Display(enumName),
                Display(name),
                ResolveItemIcon(point, catalog),
                FormatPosition(point["position"] as JObject ?? point),
                point));
        }

        CheatFieldCatapultRow? selected = _fieldCatapultRows.FirstOrDefault(row =>
            string.Equals(row.RuntimeId, selectedRuntimeId, StringComparison.Ordinal))
            ?? _fieldCatapultRows.FirstOrDefault();
        _fieldCatapultGrid.SelectedItem = selected;
        if (selected != null) _fieldCatapultGrid.ScrollIntoView(selected);
        UpdateFieldCatapultSummary();
    }

    private void UpdateFieldCatapultSummary()
    {
        _fieldCatapultSummary.Text = _fieldCatapultRows.Count == 0
            ? "场上没有弹射点"
            : $"场上共 {_fieldCatapultRows.Count} 个；可按运行时 ID 精确删除";
        ApplyAvailability();
    }

    private void EntitySelectionChanged(
        DataGrid grid,
        CatalogPickerControl attributePicker,
        TextBlock currentValue,
        CheatNumericInput input)
    {
        if (_loadingEntities) return;
        JObject? entity = SelectedEntity(grid);
        List<CatalogPickerItem> attributes = new();
        foreach (JObject attributeJson in entity?["attributes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            AttributeItem attribute = AttributeItem.FromJson(attributeJson);
            if (string.IsNullOrWhiteSpace(attribute.Id)) continue;
            attributes.Add(new CatalogPickerItem(
                attribute.Id,
                attribute.Name,
                string.Empty,
                null,
                Array.Empty<string>(),
                attribute));
        }

        attributePicker.SetItems(attributes);
        AttributeSelectionChanged(attributePicker, currentValue, input);
        ApplyAvailability();
    }

    private static void AttributeSelectionChanged(
        CatalogPickerControl picker,
        TextBlock currentValue,
        CheatNumericInput input)
    {
        if (picker.SelectedCatalogItem?.Payload is not AttributeItem attribute)
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

    private void UpdateVehicleEnchantmentEditor(bool copyCurrentToInput)
    {
        if (_loadingEntities) return;
        CatalogPickerItem? selected = _vehicleEnchantmentCatalog.SelectedCatalogItem;
        JObject? vehicle = SelectedEntity(_vehicleGrid);
        if (selected == null || vehicle == null)
        {
            _vehicleEnchantmentCurrent.Text = "当前等级 -";
            return;
        }

        if (vehicle["enchantments"] is not JArray enchantments)
        {
            _vehicleEnchantmentCurrent.Text = "当前等级未知";
            return;
        }

        JObject? current = enchantments.OfType<JObject>().FirstOrDefault(item => string.Equals(
            item.Value<string>("id") ?? item.Value<string>("enchantmentId"),
            selected.Id,
            StringComparison.Ordinal));
        int level = Math.Max(0, current?.Value<int?>("level") ?? 0);
        int effectiveLevel = Math.Max(level, current?.Value<int?>("effectiveLevel") ?? level);
        _vehicleEnchantmentCurrent.Text = effectiveLevel == level
            ? $"当前等级 {level}"
            : $"当前 {level} / 生效 {effectiveLevel}";
        if (copyCurrentToInput)
        {
            _vehicleEnchantmentLevel.Value = Math.Clamp(level, _vehicleEnchantmentLevel.Minimum, _vehicleEnchantmentLevel.Maximum);
        }
    }

    private void ShowLocalError(string message)
    {
        _lastMessage = message;
        _lastMessageIsError = true;
        ApplyAvailability();
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
            checkBox.IsChecked = value;
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

    private void AddMutationControls(params UIElement[] controls) => _mutationControls.AddRange(controls);

    private static JObject? SelectedEntity(DataGrid? grid) => grid?.SelectedItem switch
    {
        CheatVehicleRow vehicle => vehicle.Payload,
        CheatEnemyRow enemy => enemy.Payload,
        _ => null
    };

    private static void SetMaximum(CheatNumericInput input, int? maximum)
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

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
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
        public static AttributeItem FromJson(JObject item)
        {
            string id = item.Value<string>("id") ?? string.Empty;
            string name = item.Value<string>("name") ?? id;
            return new AttributeItem(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                item.Value<string>("kind") ?? "float",
                ToDecimal(item["value"], 0),
                ToDecimal(item["baseValue"], ToDecimal(item["value"], 0)),
                ToDecimal(item["minimum"], DefaultAttributeMinimum),
                ToDecimal(item["maximum"], DefaultAttributeMaximum));
        }
    }
}

internal sealed record CheatEnchantmentSelection(CatalogPickerItem Item, int Level)
{
    public ImageSource? Icon => Item.Icon;
    public string Name => Item.DisplayName;
    public string EnumName => Item.EnumName;
    public string Id => Item.Id;
}

internal sealed record CheatVehicleRow(
    string VehicleId,
    string EnumName,
    string Name,
    string Level,
    ImageSource? Icon,
    IReadOnlyList<CheatEntityEnchantmentRow> Enchantments,
    string Position,
    JObject Payload);

internal sealed record CheatEnemyRow(
    string RuntimeId,
    string EnumName,
    string Name,
    ImageSource? Icon,
    string Health,
    string Position,
    JObject Payload);

internal sealed record CheatEntityEnchantmentRow(ImageSource? Icon, string Label, string LevelLabel);

internal sealed record CheatFieldCatapultRow(
    string RuntimeId,
    string EnumName,
    string Name,
    ImageSource? Icon,
    string Position,
    JObject Payload);

internal sealed class CheatSpawnPointRow
{
    public int Number { get; set; }

    public string PointId { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public decimal XValue { get; set; }

    public decimal YValue { get; set; }

    public decimal ZValue { get; set; }

    public string X => Format(XValue);

    public string Y => Format(YValue);

    public string Z => Format(ZValue);

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
