using System.Runtime.ExceptionServices;
using System.Windows;
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

                AssertTabFits(form, 0);
                AssertTopAligned(form, "_enchantmentCatalog", "_enchantmentLevel", "_addEnchantmentButton");

                AssertTabFits(form, 2);
                AssertTopAligned(form, "_vehicleAttribute", "_vehicleCurrentValueFrame", "_vehicleAttributeValue", "_modifyVehicleButton");
                AssertTopAligned(form, "_vehicleEnchantmentCatalog", "_vehicleEnchantmentCurrentFrame", "_vehicleEnchantmentLevel", "_setVehicleEnchantmentButton");
                AssertTopAligned(form, "_enemyAttribute", "_enemyCurrentValueFrame", "_enemyAttributeValue", "_modifyEnemyButton");

                AssertTabFits(form, 3);
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
                form.SelectDemoTab(3);
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
                form.SelectDemoTab(2);
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
    public void RemoveOwnedCatapult_UsesSelectedRuntimePointDataId()
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

                CatalogPickerControl owned = Assert.IsType<CatalogPickerControl>(
                    form.FindName("_ownedCatapultCatalog"));
                Assert.Equal(2, owned.ItemCount);
                Assert.Equal("point-normal-poison", owned.SelectedId);

                Button remove = Assert.IsType<Button>(form.FindName("_removeCatapultButton"));
                Assert.True(remove.IsEnabled);
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher();

                JObject payload = Assert.Single(
                    calls,
                    call => call.Command == CheatCommands.RemoveCatapultPoint).Payload!;
                Assert.Equal("point-normal-poison", payload.Value<string>("catapultPointId"));
                Assert.Equal("FreePoint", payload.Value<string>("disposableId"));
                Assert.Equal(1, payload.Value<int>("count"));
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
                form.SelectDemoTab(3);
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
}
