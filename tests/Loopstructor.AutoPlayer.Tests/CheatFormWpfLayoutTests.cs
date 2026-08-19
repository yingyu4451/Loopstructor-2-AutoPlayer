using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

[Collection("WPF UI")]
public sealed class CheatFormWpfLayoutTests
{
    [Fact]
    public void ToolWindow_IsConfiguredAsIndependentTopLevelWindow()
    {
        RunSta(() =>
        {
            Window manager = new()
            {
                Width = 320,
                Height = 240,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            Window window = new()
            {
                Width = 320,
                Height = 240,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true
            };

            try
            {
                MainForm.ConfigureIndependentToolWindow(window);
                manager.Show();
                window.Show();
                manager.WindowState = WindowState.Minimized;
                PumpDispatcher();

                Assert.Null(window.Owner);
                Assert.True(window.ShowInTaskbar);
                Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
                Assert.False(window.Topmost);
                Assert.True(window.IsVisible);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally
            {
                window.Close();
                manager.Close();
            }
        });
    }

    [Fact]
    public void MinimumWindow_AllTabsFitHorizontally_AndSiblingEditorsAlign()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.Activate();
                PumpDispatcher();

                TabControl navigation = Assert.IsType<TabControl>(form.FindName("_tabs"));
                TabItem[] navigationTabs = navigation.Items.Cast<TabItem>().ToArray();
                Assert.Equal(new[] { "战车", "道具", "遗物", "战斗", "对象属性", "生成" }, navigationTabs.Select(tab => tab.Header));
                Assert.Equal(6, navigationTabs.Length);
                Assert.All(navigationTabs, tab => Assert.Equal(navigationTabs[0].ActualWidth, tab.ActualWidth, precision: 2));
                Assert.IsType<Button>(form.FindName("_catalogRefreshButton"));
                Assert.Null(form.FindName("_versionLabel"));
                Assert.Null(form.FindName("_catalogSummary"));
                Assert.Null(form.FindName("_spawnCatalogRefreshButton"));

                AssertTabFits(form, 0);
                AssertMinimumWidth(form, 420, "_enchantmentSelector");

                AssertTabFits(form, 1);
                AssertMinimumWidth(form, 120, "_clearBackpackCatapultsButton", "_clearFieldCatapultsButton");

                AssertTabFits(form, 2);
                AssertMinimumWidth(form, 120, "_grantAllRelicsButton", "_removeAllRelicsButton");

                AssertTabFits(form, 3);
                AssertTopAligned(form, "_enemyIdOverlayCheck", "_enemyBuffOverlayCheck");
                AssertMinimumWidth(form, 180, "_enemyIdOverlayCheck", "_enemyBuffOverlayCheck");

                AssertTabFits(form, 4);
                AssertTopAligned(form, "_vehicleAttribute", "_vehicleCurrentValueFrame", "_vehicleAttributeValue", "_modifyVehicleButton");
                AssertTopAligned(form, "_vehicleEnchantmentCatalog", "_vehicleEnchantmentCurrentFrame", "_vehicleEnchantmentLevel", "_setVehicleEnchantmentButton");
                AssertTopAligned(form, "_enemyAttribute", "_enemyCurrentValueFrame", "_enemyAttributeValue", "_modifyEnemyButton");

                AssertTabFits(form, 5);
                AssertTopAligned(form, "_enemyCatalog", "_enemyLevel", "_followCurrentLevelCheck", "_enemyCount");
                AssertTopAligned(form, "_spawnX", "_spawnY", "_spawnZ", "_spawnRadius", "_capturePointButton", "_cancelCaptureButton", "_addSpawnPointButton");
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GlobalCatalogRefresh_IsSingleAndRemainsVisibleAcrossTabs()
    {
        RunSta(() =>
        {
            List<string> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add(command);
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                PumpDispatcher();
                calls.Clear();

                Button refresh = Assert.IsType<Button>(form.FindName("_catalogRefreshButton"));
                TabControl tabs = Assert.IsType<TabControl>(form.FindName("_tabs"));
                for (int index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    PumpDispatcher();
                    Assert.True(refresh.IsVisible);
                }

                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));
                Assert.Single(calls, command => command == CheatCommands.QueryCatalog);
                Assert.Equal(
                    "演示资源目录已加载。",
                    Assert.IsType<TextBlock>(form.FindName("_toastText")).Text);
                Assert.True(Assert.IsType<Border>(form.FindName("_toastHost")).IsVisible);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void PersistentCheatState_IsRenderedInRunControlWithoutLegacyBanner()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                AutoPlayerStatus status = DemoData.CheatStatus();
                form.UpdateSession(true, DemoData.CheatHello(), status);
                form.Show();
                PumpDispatcher();

                TextBlock state = Assert.IsType<TextBlock>(form.FindName("_runControlStateText"));
                Border badge = Assert.IsType<Border>(form.FindName("_runControlStateBadge"));
                Assert.Equal("已启用", state.Text);
                Assert.Null(form.FindName("_statusBanner"));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(badge)));

                status.CheatUsed = true;
                form.UpdateSession(true, DemoData.CheatHello(), status);
                Assert.Equal("已启用 · 已标记", state.Text);

                status.RunState = AutoPlayerRunState.Running;
                form.UpdateSession(true, DemoData.CheatHello(), status);
                Assert.Equal("监视中 · 写操作锁定", state.Text);

                status.CheatModeEnabled = false;
                form.UpdateSession(true, DemoData.CheatHello(), status);
                Assert.Equal("自动游玩中", state.Text);

                status.RunState = AutoPlayerRunState.Standby;
                status.CheatUsed = false;
                form.UpdateSession(true, DemoData.CheatHello(), status);
                Assert.Equal("未启用", state.Text);

                form.UpdateSession(false, DemoData.CheatHello(), status);
                Assert.Equal("未连接", state.Text);

                status.CheatAvailable = false;
                form.UpdateSession(true, DemoData.CheatHello(), status);
                Assert.Equal("不可用", state.Text);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void ModeToggle_UpdatesPersistentStateWithoutEnqueuingToast()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));

                CheckBox enable = Assert.IsType<CheckBox>(form.FindName("_enableCheck"));
                enable.IsChecked = false;
                enable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));

                Assert.Equal("未启用", Assert.IsType<TextBlock>(form.FindName("_runControlStateText")).Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(form.FindName("_toastHost")).Visibility);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void TransientToasts_AreNonInteractiveAndDisplayInFifoOrderForThreeSecondsEach()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                PumpDispatcher();

                System.Reflection.MethodInfo showError = typeof(CheatForm).GetMethod(
                    "ShowLocalError",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                showError.Invoke(form, new object[] { "第一条错误" });
                showError.Invoke(form, new object[] { "第二条错误" });
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));

                Border toast = Assert.IsType<Border>(form.FindName("_toastHost"));
                TextBlock text = Assert.IsType<TextBlock>(form.FindName("_toastText"));
                Assert.True(toast.IsVisible);
                Assert.False(toast.IsHitTestVisible);
                Assert.Equal("第一条错误", text.Text);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(2900));
                Assert.Equal("第一条错误", text.Text);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(500));
                Assert.True(toast.IsVisible);
                Assert.Equal("第二条错误", text.Text);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(3400));
                Assert.Equal(Visibility.Collapsed, toast.Visibility);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void VehicleQuickSelection_SendsExactLevelIdAndQuantity()
    {
        RunSta(() =>
        {
            List<(string Command, JObject? Payload)> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add((command, payload?.DeepClone() as JObject));
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.LoadDemoCatalogAsync().GetAwaiter().GetResult();
                PumpDispatcher();
                calls.Clear();

                VehicleQuickSelectorControl selector = Assert.IsType<VehicleQuickSelectorControl>(form.FindName("_vehicleCatalog"));
                SelectVehicle(selector, "Link_ElectricFork_L3");
                CheatNumericInput count = Assert.IsType<CheatNumericInput>(form.FindName("_vehicleCount"));
                count.Value = 3;
                Assert.IsType<Button>(form.FindName("_grantVehicleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                JObject payload = Assert.Single(calls, call => call.Command == CheatCommands.GrantVehicle).Payload!;
                Assert.Equal("Link_ElectricFork_L3", payload.Value<string>("vehicleId"));
                Assert.Equal(3, payload.Value<int>("count"));
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void SelectedEnchantments_RenderAsReadOnlyWrappedIcons_WithDelayedGameDetails()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.LoadDemoCatalogAsync().GetAwaiter().GetResult();
                PumpDispatcher();

                EnchantmentSelectorControl selector = Assert.IsType<EnchantmentSelectorControl>(
                    form.FindName("_enchantmentSelector"));
                System.Collections.IEnumerable source = (System.Collections.IEnumerable)typeof(EnchantmentSelectorControl)
                    .GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(selector)!;
                EnchantmentChoice selected = source.Cast<EnchantmentChoice>().First();
                selected.Level = 12;
                typeof(EnchantmentSelectorControl)
                    .GetMethod("RefreshSummary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(selector, null);
                PumpDispatcher();

                ItemsControl summary = Assert.IsType<ItemsControl>(form.FindName("_selectedEnchantmentSummary"));
                Assert.Single(VisualDescendants<WrapPanel>(summary));
                EnchantmentChoice rendered = Assert.IsType<EnchantmentChoice>(Assert.Single(summary.Items));
                Assert.Same(selected, rendered);
                FrameworkElement container = Assert.IsAssignableFrom<FrameworkElement>(
                    summary.ItemContainerGenerator.ContainerFromItem(rendered));
                Border tile = VisualDescendants<Border>(container)
                    .Single(border => Math.Abs(border.Width - 48) < 0.1 && Math.Abs(border.Height - 48) < 0.1);

                Assert.Equal(1000, ToolTipService.GetInitialShowDelay(tile));
                ToolTip tooltip = Assert.IsType<ToolTip>(tile.ToolTip);
                Assert.IsType<CatalogDetailToolTip>(tooltip.Content);
                string[] visibleTexts = VisualDescendants<TextBlock>(tile)
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                Assert.Contains("12", visibleTexts);
                Assert.DoesNotContain(selected.Item.DisplayName, visibleTexts);
                Assert.DoesNotContain(selected.Item.EnumName, visibleTexts);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void EnemyBuffOverlay_SendsIndependentVisibleCommand()
    {
        RunSta(() =>
        {
            List<(string Command, JObject? Payload)> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add((command, payload?.DeepClone() as JObject));
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(3);
                PumpDispatcher();

                CheckBox idOverlay = Assert.IsType<CheckBox>(form.FindName("_enemyIdOverlayCheck"));
                CheckBox buffOverlay = Assert.IsType<CheckBox>(form.FindName("_enemyBuffOverlayCheck"));
                Assert.False(idOverlay.IsChecked);
                Assert.False(buffOverlay.IsChecked);

                buffOverlay.IsChecked = true;
                buffOverlay.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                PumpDispatcher();

                JObject payload = Assert.Single(
                    calls,
                    call => call.Command == CheatCommands.SetEnemyBuffOverlay).Payload!;
                Assert.True(payload.Value<bool>("visible"));
                Assert.True(buffOverlay.IsChecked);
                Assert.False(idOverlay.IsChecked);

                idOverlay.IsChecked = true;
                idOverlay.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                PumpDispatcher();

                Assert.Single(calls, call => call.Command == CheatCommands.SetEnemyIdOverlay);
                Assert.True(idOverlay.IsChecked);
                Assert.True(buffOverlay.IsChecked);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void EnemyBuffOverlay_FailedCommandUsesReturnedPluginState()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
            {
                if (!string.Equals(command, CheatCommands.SetEnemyBuffOverlay, StringComparison.Ordinal))
                {
                    return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
                }

                AutoPlayerStatus status = DemoData.CheatStatus();
                status.EnemyBuffsVisible = true;
                return Task.FromResult<ControlResponse?>(new ControlResponse
                {
                    Success = false,
                    Message = "模拟失败",
                    Status = status
                });
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(3);
                PumpDispatcher();

                CheckBox buffOverlay = Assert.IsType<CheckBox>(form.FindName("_enemyBuffOverlayCheck"));
                buffOverlay.IsChecked = true;
                buffOverlay.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                PumpDispatcher();

                Assert.True(buffOverlay.IsChecked);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Theory]
    [InlineData(AutoPlayerRunState.Running)]
    [InlineData(AutoPlayerRunState.Paused)]
    public void ActiveAutoPlay_EnablesObservationControlsAndLocksCheatWrites(AutoPlayerRunState runState)
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                AutoPlayerStatus status = DemoData.CheatStatus();
                status.RunState = runState;
                status.CheatModeEnabled = true;
                form.UpdateSession(true, DemoData.CheatHello(), status);

                Assert.True(Assert.IsType<CheckBox>(form.FindName("_enableCheck")).IsEnabled);
                Assert.True(Assert.IsType<Button>(form.FindName("_catalogRefreshButton")).IsEnabled);
                Assert.True(Assert.IsType<VehicleQuickSelectorControl>(form.FindName("_vehicleCatalog")).IsEnabled);
                Assert.True(Assert.IsType<CatalogPickerControl>(form.FindName("_enemyCatalog")).IsEnabled);
                Assert.True(Assert.IsType<Button>(form.FindName("_vehicleRefreshButton")).IsEnabled);
                Assert.True(Assert.IsType<Button>(form.FindName("_enemyRefreshButton")).IsEnabled);
                Assert.True(Assert.IsType<DataGrid>(form.FindName("_vehicleGrid")).IsEnabled);
                Assert.True(Assert.IsType<DataGrid>(form.FindName("_enemyGrid")).IsEnabled);
                Assert.True(Assert.IsType<CheckBox>(form.FindName("_enemyIdOverlayCheck")).IsEnabled);
                Assert.True(Assert.IsType<CheckBox>(form.FindName("_enemyBuffOverlayCheck")).IsEnabled);

                Assert.False(Assert.IsType<Button>(form.FindName("_grantVehicleButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_grantAllRelicsButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_removeAllRelicsButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_clearConsumablesButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_clearBackpackCatapultsButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_clearFieldCatapultsButton")).IsEnabled);
                Assert.False(Assert.IsType<CheckBox>(form.FindName("_fieldCatapultDeleteModeCheck")).IsEnabled);
                CatalogActionGridControl disposableActions = Assert.IsType<CatalogActionGridControl>(form.FindName("_disposableActions"));
                CatalogActionGridControl relicActions = Assert.IsType<CatalogActionGridControl>(form.FindName("_relicActions"));
                CatalogActionGridControl catapultActions = Assert.IsType<CatalogActionGridControl>(form.FindName("_catapultActions"));
                Assert.True(disposableActions.IsEnabled);
                Assert.True(relicActions.IsEnabled);
                Assert.True(catapultActions.IsEnabled);
                Assert.False(disposableActions.IsActionEnabled);
                Assert.False(relicActions.IsActionEnabled);
                Assert.False(catapultActions.IsActionEnabled);
                Assert.False(Assert.IsType<Button>(form.FindName("_clearEnemiesButton")).IsEnabled);
                Assert.False(Assert.IsType<CheckBox>(form.FindName("_baseGodModeCheck")).IsEnabled);
                Assert.False(Assert.IsType<CheckBox>(form.FindName("_mapSkipCheck")).IsEnabled);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void ResourceBulkDeletes_ExecuteImmediatelyOnce_AndFieldDeleteLivesOnlyOnResourcePage()
    {
        RunSta(() =>
        {
            List<(string Command, JObject? Payload)> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add((command, payload?.DeepClone() as JObject));
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(1);
                PumpDispatcher();

                Assert.Null(form.FindName("_fieldCatapultGrid"));
                Assert.Null(form.FindName("_removeFieldCatapultButton"));

                Assert.IsType<Button>(form.FindName("_clearConsumablesButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(form.FindName("_clearBackpackCatapultsButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(form.FindName("_clearFieldCatapultsButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(form.FindName("_removeAllRelicsButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                CheckBox deleteMode = Assert.IsType<CheckBox>(form.FindName("_fieldCatapultDeleteModeCheck"));
                deleteMode.IsChecked = true;
                deleteMode.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                PumpDispatcher();

                Assert.Single(calls, call => call.Command == CheatCommands.ClearConsumables);
                Assert.Single(calls, call => call.Command == CheatCommands.ClearBackpackCatapultPoints);
                Assert.Single(calls, call => call.Command == CheatCommands.ClearFieldCatapultPoints);
                Assert.Single(calls, call => call.Command == CheatCommands.RemoveAllRelics);
                JObject modePayload = Assert.Single(
                    calls,
                    call => call.Command == CheatCommands.SetFieldCatapultDeleteMode).Payload!;
                Assert.True(modePayload.Value<bool>("enabled"));
                Assert.True(deleteMode.IsChecked);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void SpawnPointList_AddsManualPoints_AndDefaultsToCurrentLevel()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 1100,
                Height = 760,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(5);
                PumpDispatcher();

                CheckBox follow = Assert.IsType<CheckBox>(form.FindName("_followCurrentLevelCheck"));
                CheatNumericInput level = Assert.IsType<CheatNumericInput>(form.FindName("_enemyLevel"));
                Assert.True(follow.IsChecked);
                Assert.False(level.IsEnabled);

                Assert.IsType<CheatNumericInput>(form.FindName("_spawnX")).Value = 12.5m;
                Assert.IsType<CheatNumericInput>(form.FindName("_spawnY")).Value = -4.25m;
                Assert.IsType<CheatNumericInput>(form.FindName("_spawnZ")).Value = 3m;
                Button add = Assert.IsType<Button>(form.FindName("_addSpawnPointButton"));
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                DataGrid points = Assert.IsType<DataGrid>(form.FindName("_spawnPointGrid"));
                CheatSpawnPointRow point = Assert.IsType<CheatSpawnPointRow>(Assert.Single(points.Items));
                Assert.Equal("手工坐标", point.Source);
                Assert.Equal("12.5", point.X);
                Assert.Equal("-4.25", point.Y);
                Assert.Equal("3", point.Z);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void ObjectRows_ExposeChineseNamesEnumsIconsAndExistingEnchantments()
    {
        RunSta(() =>
        {
            CheatForm form = new((command, payload) =>
                Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 1100,
                Height = 760,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(4);
                PumpDispatcher();

                Assert.IsType<Button>(form.FindName("_vehicleRefreshButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(form.FindName("_enemyRefreshButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                DataGrid vehicles = Assert.IsType<DataGrid>(form.FindName("_vehicleGrid"));
                CheatVehicleRow vehicle = Assert.IsType<CheatVehicleRow>(Assert.Single(vehicles.Items));
                Assert.Equal("雷叉", vehicle.Name);
                Assert.Equal("Link_ElectricFork_L2", vehicle.EnumName);
                Assert.Collection(
                    vehicle.Enchantments,
                    enchantment => Assert.Contains("中毒", enchantment.Label, StringComparison.Ordinal),
                    enchantment => Assert.Contains("能量", enchantment.Label, StringComparison.Ordinal));

                DataGrid enemies = Assert.IsType<DataGrid>(form.FindName("_enemyGrid"));
                CheatEnemyRow enemy = Assert.IsType<CheatEnemyRow>(Assert.Single(enemies.Items));
                Assert.Equal("巨型骷髅", enemy.Name);
                Assert.Equal("SkullGiant", enemy.EnumName);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void ResourceCatapultCard_RemovesSingleDeletePickerAndKeepsBulkScopes()
    {
        RunSta(() =>
        {
            List<(string Command, JObject? Payload)> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add((command, payload?.DeepClone() as JObject));
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 1100,
                Height = 760,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                PumpDispatcher();

                CatalogActionGridControl actions = Assert.IsType<CatalogActionGridControl>(form.FindName("_catapultActions"));
                Assert.Equal(2, actions.ItemCount);
                Assert.Null(form.FindName("_ownedCatapultCatalog"));
                Assert.Null(form.FindName("_removeCatapultButton"));
                Assert.DoesNotContain(calls, call => call.Command == CheatCommands.RemoveCatapultPoint);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void SpawnCommand_SendsEveryPoint_AndUsesCurrentLevelByDefault()
    {
        RunSta(() =>
        {
            List<(string Command, JObject? Payload)> calls = new();
            CheatForm form = new((command, payload) =>
            {
                calls.Add((command, payload?.DeepClone() as JObject));
                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, payload));
            })
            {
                Width = 1100,
                Height = 760,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());
                form.Show();
                form.SelectDemoTab(5);
                PumpDispatcher();

                CheatNumericInput x = Assert.IsType<CheatNumericInput>(form.FindName("_spawnX"));
                Button add = Assert.IsType<Button>(form.FindName("_addSpawnPointButton"));
                x.Value = 10m;
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                x.Value = 20m;
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                Button spawn = Assert.IsType<Button>(form.FindName("_spawnEnemyButton"));
                Assert.True(spawn.IsEnabled);
                spawn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                JObject payload = Assert.Single(calls, call => call.Command == CheatCommands.SpawnEnemy).Payload!;
                Assert.True(payload.Value<bool>("useCurrentLevel"));
                Assert.Equal("current", payload.Value<string>("levelMode"));
                Assert.Null(payload["level"]);
                JArray points = Assert.IsType<JArray>(payload["points"]);
                Assert.Equal(new[] { 10d, 20d }, points.OfType<JObject>().Select(point => point.Value<double>("x")));
                Assert.Equal("CommonMonster", payload.Value<string>("enumName"));
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void GrantAllRelics_DoesNotRequireSelection_SendsOneCommandAndPollsUntilCompleted()
    {
        RunSta(() =>
        {
            List<string> calls = new();
            int queryStateCalls = 0;
            CheatForm form = new((command, _) =>
            {
                calls.Add(command);
                if (string.Equals(command, CheatCommands.GrantAllRelics, StringComparison.Ordinal))
                {
                    return Task.FromResult<ControlResponse?>(GrantAllRelicsResponse(
                        "running",
                        processed: 0,
                        total: 2));
                }

                if (string.Equals(command, CheatCommands.QueryState, StringComparison.Ordinal))
                {
                    queryStateCalls++;
                    return Task.FromResult<ControlResponse?>(GrantAllRelicsResponse(
                        "completed",
                        processed: 2,
                        total: 2));
                }

                return Task.FromResult<ControlResponse?>(DemoData.CheatResponse(command, null));
            })
            {
                Width = 980,
                Height = 680,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.UpdateSession(true, DemoData.CheatHello(), DemoData.CheatStatus());

                Button grantAll = Assert.IsType<Button>(form.FindName("_grantAllRelicsButton"));
                Assert.True(grantAll.IsEnabled);
                grantAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                Assert.Single(calls, command => command == CheatCommands.GrantAllRelics);
                Assert.False(grantAll.IsEnabled);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(550));

                Assert.Single(calls, command => command == CheatCommands.GrantAllRelics);
                Assert.True(queryStateCalls >= 1);
                Assert.True(grantAll.IsEnabled);
                TextBlock status = Assert.IsType<TextBlock>(form.FindName("_grantAllRelicsStatus"));
                Assert.Contains("2 / 2", status.Text, StringComparison.Ordinal);
            }
            finally
            {
                form.Close();
            }
        });
    }

    private static ControlResponse GrantAllRelicsResponse(
        string state,
        int processed,
        int total) => new()
    {
        Success = true,
        Status = DemoData.CheatStatus(),
        Data = new JObject
        {
            ["grantAllRelics"] = new JObject
            {
                ["state"] = state,
                ["processedCount"] = processed,
                ["totalCount"] = total,
                ["grantedCount"] = processed,
                ["skippedCount"] = 0,
                ["failedCount"] = 0
            },
            ["ownedRelics"] = new JArray()
        }
    };

    private static void AssertTabFits(CheatForm form, int index)
    {
        form.SelectDemoTab(index);
        form.UpdateLayout();
        PumpDispatcher();

        TabControl tabs = Assert.IsType<TabControl>(form.FindName("_tabs"));
        TabItem tab = Assert.IsType<TabItem>(tabs.SelectedItem);
        ScrollViewer page = Assert.IsType<ScrollViewer>(tab.Content);
        Assert.True(
            page.ExtentWidth <= page.ViewportWidth + 1,
            $"选项卡 {index} 存在水平裁剪：内容 {page.ExtentWidth:0.##}，视口 {page.ViewportWidth:0.##}。");
    }

    private static void AssertTopAligned(FrameworkElement ancestor, params string[] names)
    {
        double[] tops = names.Select(name =>
        {
            FrameworkElement element = Assert.IsAssignableFrom<FrameworkElement>(ancestor.FindName(name));
            Assert.True(element.ActualWidth > 0, $"{name} 没有获得可见宽度。");
            Assert.True(element.ActualHeight > 0, $"{name} 没有获得可见高度。");
            return element.TransformToAncestor(ancestor).Transform(new Point()).Y;
        }).ToArray();

        Assert.True(
            tops.Max() - tops.Min() <= 1,
            $"兄弟控件未水平对齐：{string.Join(", ", names.Zip(tops, (name, top) => $"{name}={top:0.##}"))}");
    }

    private static void AssertMinimumWidth(FrameworkElement ancestor, double minimumWidth, params string[] names)
    {
        foreach (string name in names)
        {
            FrameworkElement element = Assert.IsAssignableFrom<FrameworkElement>(ancestor.FindName(name));
            Assert.True(
                element.ActualWidth >= minimumWidth,
                $"{name} 的可见宽度只有 {element.ActualWidth:0.##}，可能裁切文字。");
        }
    }

    private static void SelectVehicle(VehicleQuickSelectorControl selector, string id)
    {
        CatalogPickerItem item = selector.FindItem(id)!;
        System.Collections.IEnumerable types = (System.Collections.IEnumerable)typeof(VehicleQuickSelectorControl)
            .GetField("_types", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(selector)!;
        VehicleTypeChoice type = types.Cast<VehicleTypeChoice>().Single(choice => choice.Key == item.TypeKey);
        typeof(VehicleQuickSelectorControl)
            .GetMethod("TypeButton_OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(selector, new object[] { new Button { DataContext = type }, new RoutedEventArgs() });

        System.Collections.IEnumerable series = (System.Collections.IEnumerable)typeof(VehicleQuickSelectorControl)
            .GetField("_series", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(selector)!;
        VehicleLevelChoice level = series.Cast<VehicleSeriesChoice>()
            .SelectMany(choice => choice.Levels)
            .Single(choice => choice.Item.Id == id);
        typeof(VehicleQuickSelectorControl)
            .GetMethod("LevelButton_OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(selector, new object[] { new Button { DataContext = level }, new RoutedEventArgs() });
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF 作弊工具布局测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.ApplicationIdle)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
