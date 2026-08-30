using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;
using DrawingSize = System.Drawing.Size;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerDemoTests
{
    [Fact]
    public void MainWindow_UsesCompactAutomationLayout_AndLogTelemetryTabs()
    {
        RunSta(() =>
        {
            MainForm form = new(ManagerLaunchOptions.Parse(new[] { "--demo" }))
            {
                Width = 1400,
                Height = 860,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };

            try
            {
                form.Show();
                PumpDispatcher();

                Assert.Null(form.FindName("_modeAvailability"));
                Assert.Null(form.FindName("_autoUpdateCheck"));
                Assert.Null(form.FindName("_logHeightSplitter"));

                TabControl tabs = Assert.IsType<TabControl>(form.FindName("_monitorTabs"));
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.Equal(new[] { "运行日志", "运行遥测" }, tabs.Items.Cast<TabItem>().Select(item => item.Header));
                RichTextBox logs = Assert.IsType<RichTextBox>(form.FindName("_logs"));
                Assert.IsType<ItemsControl>(form.FindName("_telemetryItems"));
                logs.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("切换后保留")));
                tabs.SelectedIndex = 1;
                PumpDispatcher();
                tabs.SelectedIndex = 0;
                PumpDispatcher();
                Assert.Contains(
                    "切换后保留",
                    new System.Windows.Documents.TextRange(logs.Document.ContentStart, logs.Document.ContentEnd).Text,
                    StringComparison.Ordinal);

                FrameworkElement character = Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_characterField"));
                FrameworkElement story = Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_skipStoryField"));
                FrameworkElement priority = Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_decisionPriorityField"));
                Assert.Equal(Visibility.Visible, character.Visibility);
                Assert.Equal(0, Grid.GetColumn(character));
                Assert.Equal(1, Grid.GetColumn(story));
                Assert.Equal(2, Grid.GetColumn(priority));

                FrameworkElement root = Assert.IsAssignableFrom<FrameworkElement>(form.Content);
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_secondaryRunOptions")));
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_timelineScroll")));
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_monitorTabs")));
                SaveScreenshot(root, 1400, 860);

                form.Width = 1100;
                form.Height = 680;
                PumpDispatcher();
                root.UpdateLayout();
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_secondaryRunOptions")));
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_timelineScroll")));
                AssertInside(root, Assert.IsAssignableFrom<FrameworkElement>(form.FindName("_monitorTabs")));
                SaveScreenshot(root, 1100, 680);

                CheckBox continueProfile = Assert.IsType<CheckBox>(form.FindName("_continueProfile"));
                continueProfile.IsChecked = true;
                PumpDispatcher();
                Assert.Equal(Visibility.Collapsed, character.Visibility);
                Assert.Equal(0, Grid.GetColumn(story));
                Assert.Equal(1, Grid.GetColumn(priority));
                Grid secondaryOptions = Assert.IsType<Grid>(form.FindName("_secondaryRunOptions"));
                Assert.Equal(0, secondaryOptions.ColumnDefinitions[2].Width.Value);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void ManagerSettings_NoLongerExposeOptionalStartupUpdateSwitch()
    {
        Assert.Null(typeof(ManagerSettings).GetProperty("CheckUpdatesOnStart", BindingFlags.Instance | BindingFlags.Public));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void StartupUpdateCheck_IsAutomaticExceptInDemoMode(
        bool demoMode,
        bool configured,
        bool expected)
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(demoMode ? new[] { "--demo" } : Array.Empty<string>());
        Assert.Equal(expected, MainForm.ShouldCheckForUpdates(options, configured));
    }

    [Theory]
    [InlineData(AutoPlayerRunState.Running, false)]
    [InlineData(AutoPlayerRunState.Paused, false)]
    [InlineData(AutoPlayerRunState.Standby, true)]
    [InlineData(AutoPlayerRunState.Completed, true)]
    public void AutomationSetupRefresh_IsSuppressedDuringActiveRun(
        AutoPlayerRunState runState,
        bool expected)
    {
        Assert.Equal(expected, MainForm.ShouldRefreshAutomationSetup(new AutoPlayerStatus
        {
            RunState = runState
        }));
    }

    [Fact]
    public void AutomationSpeed_DefaultUsesNormalGameSpeed()
    {
        AutomationRunOptions options = new();
        ManagerSettings settings = new();

        Assert.True(options.OverrideGameSpeed);
        Assert.Equal(0, options.SpeedState);
        Assert.True(settings.OverrideGameSpeed);
        Assert.Equal(0, settings.SpeedState);
        Assert.Equal(1, MainForm.SpeedSelectionIndex(settings.OverrideGameSpeed, settings.SpeedState));
    }

    [Fact]
    public void AutomationSpeed_LegacyRunOptionsResetHistoricalThreeTimesDefaultToNormalSpeed()
    {
        AutomationRunOptions options = new()
        {
            GameSpeedControlVersion = 0,
            OverrideGameSpeed = true,
            SpeedState = 2
        };

        AutoPlayerGameSpeed.Normalize(options);

        Assert.Equal(AutoPlayerGameSpeed.CurrentOptionsVersion, options.GameSpeedControlVersion);
        Assert.True(options.OverrideGameSpeed);
        Assert.Equal(0, options.SpeedState);
    }

    [Fact]
    public void AutomationSpeed_CurrentFollowGameSelectionIsPreserved()
    {
        AutomationRunOptions options = new()
        {
            GameSpeedControlVersion = AutoPlayerGameSpeed.CurrentOptionsVersion,
            OverrideGameSpeed = false,
            SpeedState = 2
        };

        AutoPlayerGameSpeed.Normalize(options);

        Assert.False(options.OverrideGameSpeed);
        Assert.Equal(2, options.SpeedState);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 0)]
    [InlineData(2, true, 1)]
    [InlineData(3, true, 2)]
    public void AutomationSpeed_SelectionMapsToExplicitOverrideOnlyWhenRequested(
        int selectedIndex,
        bool expectedOverride,
        int expectedSpeedState)
    {
        Assert.Equal(expectedOverride, MainForm.ShouldOverrideGameSpeed(selectedIndex));
        Assert.Equal(expectedSpeedState, MainForm.SpeedStateFromSelectionIndex(selectedIndex));
        Assert.Equal(
            selectedIndex,
            MainForm.SpeedSelectionIndex(expectedOverride, expectedSpeedState));
    }

    [Theory]
    [InlineData("0.1.7", "AutoPlayer 版本 v0.1.7")]
    [InlineData("0.2.0-beta.1", "AutoPlayer 版本 v0.2.0-beta.1")]
    [InlineData(" 1.0.0 ", "AutoPlayer 版本 v1.0.0")]
    [InlineData("", "AutoPlayer 版本 v0.0.0")]
    public void ManagerProductInfo_FormatsPermanentVersionLabel(string version, string expected)
    {
        Assert.Equal(expected, ManagerProductInfo.FormatVersionLabel(version));
    }

    [Fact]
    public void ManagerProductInfo_UsesManagerAssemblyInformationalVersion()
    {
        string expected = typeof(ManagerProductInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single()
            .InformationalVersion;

        Assert.Equal(expected, ManagerProductInfo.Version);
        Assert.Equal(ManagerProductInfo.FormatVersionLabel(expected), ManagerProductInfo.DisplayText);
    }

    [Fact]
    public void Parse_DemoRestartRequired_EnablesDemoAndRestartState()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[]
        {
            "--demo-restart-required",
            "--screenshot-mode",
            "--window-size",
            "1280x720"
        });

        Assert.True(options.DemoMode);
        Assert.True(options.DemoRestartRequired);
        Assert.True(options.ScreenshotMode);
        Assert.Equal(new DrawingSize(1280, 720), options.WindowSize);
    }

    [Fact]
    public void Parse_Demo_DoesNotRequireRestart()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[] { "--demo" });

        Assert.True(options.DemoMode);
        Assert.False(options.DemoRestartRequired);
    }

    [Fact]
    public void Parse_DemoCheatWindow_EnablesIsolatedCheatDemo()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[]
        {
            "--demo-cheat-window",
            "--demo-cheat-tab",
            "3"
        });

        Assert.True(options.DemoMode);
        Assert.True(options.DemoCheatWindow);
        Assert.False(options.DemoRestartRequired);
        Assert.Equal(3, options.DemoCheatTab);
    }

    [Fact]
    public void Parse_RestartedAfterUpdate_EnablesArtifactCleanup()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[] { "--restarted-after-update" });

        Assert.True(options.RestartedAfterUpdate);
        Assert.False(options.DemoMode);
    }

    [Fact]
    public void Parse_DemoCheatWindow_AllowsSixthTab()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[]
        {
            "--demo-cheat-window",
            "--demo-cheat-tab",
            "5"
        });

        Assert.Equal(5, options.DemoCheatTab);
    }

    [Fact]
    public void Parse_WindowSize_AllowsCheatMinimumWidth()
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(new[]
        {
            "--demo-cheat-window",
            "--window-size",
            "980x680"
        });

        Assert.Equal(new DrawingSize(980, 680), options.WindowSize);
    }

    [Fact]
    public void CheatDemo_ExposesAuthorizedEnabledSession()
    {
        BridgeHello hello = DemoData.CheatHello();
        AutoPlayerStatus status = DemoData.CheatStatus();

        Assert.True(hello.CheatSessionAuthorized);
        Assert.True(hello.CheatAvailable);
        Assert.Equal(CheatCommands.All.Count, hello.CheatCapabilities.Count);
        Assert.True(status.CheatModeEnabled);
        Assert.Equal("clean", status.RunIntegrity);
        Assert.Contains(Path.DirectorySeparatorChar + "qa-default", status.IsolatedSaveRoot);
    }

    [Theory]
    [InlineData("剧毒", true)]
    [InlineData("poison", true)]
    [InlineData("高级 持续", true)]
    [InlineData("冰冻", false)]
    public void CatalogPickerItem_SearchesChineseNamesIdsAndTags(string query, bool expected)
    {
        CatalogPickerItem item = new(
            "PoisonDomain",
            "剧毒场域",
            "Poison Domain",
            null,
            new[] { "附魔", "高级", "持续伤害" });

        Assert.Equal(expected, item.Matches(query));
    }

    [Fact]
    public void CatalogPickerItem_LevelLabel_UsesCatalogPayload()
    {
        CatalogPickerItem item = new(
            "Link_ElectricFork",
            "雷叉",
            string.Empty,
            null,
            Array.Empty<string>(),
            new JObject { ["level"] = 3 });

        Assert.Equal("Lv.3", item.LevelLabel);
        Assert.Equal("雷叉 · Link_ElectricFork · Lv.3", item.SelectionText);
    }

    [Fact]
    public void CatalogPickerItem_LevelLabel_UsesAnyNumericIdSuffix()
    {
        CatalogPickerItem item = new(
            "Link_ElectricFork_L12",
            "雷叉",
            string.Empty,
            null,
            Array.Empty<string>());

        Assert.Equal("Lv.12", item.LevelLabel);
    }

    [Fact]
    public void CheatDemo_QueryCatalog_ContainsOnlyInitialAndUpgradedVehicleForms()
    {
        ControlResponse response = DemoData.CheatResponse(CheatCommands.QueryCatalog, null);
        JArray vehicles = Assert.IsType<JArray>(response.Data!["vehicles"]);

        Assert.True(response.Success);
        Assert.DoesNotContain(vehicles.OfType<JObject>(), item => item.Value<int>("level") == 2);
        Assert.Contains(vehicles.OfType<JObject>(), item =>
            item.Value<string>("name") == "雷叉" && item.Value<int>("level") == 1);
        Assert.Contains(vehicles.OfType<JObject>(), item =>
            item.Value<string>("name") == "雷叉" && item.Value<int>("level") == 3);
    }

    [Fact]
    public void DemoStatus_RestartVariant_ExposesRestartGateFromCompletedState()
    {
        AutoPlayerStatus status = DemoData.Status(needsProcessRestart: true);

        Assert.True(status.NeedsProcessRestart);
        Assert.Equal(AutoPlayerRunState.Completed, status.RunState);
        Assert.Equal(AutomationStage.Completed, status.Stage);
    }

    [Fact]
    public void TimelineHistory_RetainsEveryEventForScrolling()
    {
        TimelineEvent[] events = Enumerable.Range(0, 24)
            .Select(index => new TimelineEvent
            {
                TimestampUtc = DateTime.UtcNow.AddSeconds(index),
                Stage = AutomationStage.Battle,
                Kind = "action",
                Message = "历史事件 " + index
            })
            .ToArray();

        IReadOnlyList<TimelineEvent> history = MainForm.TimelineHistory(events);

        Assert.Equal(24, history.Count);
        Assert.Equal(events.Select(item => item.Message), history.Select(item => item.Message));
        Assert.NotSame(events, history);
    }

    [Fact]
    public void TimelineHistory_NullInputReturnsEmptyHistory()
    {
        Assert.Empty(MainForm.TimelineHistory(null));
    }

    [Theory]
    [InlineData(0, 100, 100, true)]
    [InlineData(400, 100, 500, true)]
    [InlineData(398, 100, 500, true)]
    [InlineData(250, 100, 500, false)]
    public void LogFollowLatest_OnlyScrollsWhenAlreadyAtBottom(
        double verticalOffset,
        double viewportHeight,
        double extentHeight,
        bool expected)
    {
        Assert.Equal(expected, MainForm.ShouldFollowLatest(verticalOffset, viewportHeight, extentHeight));
    }

    [Fact]
    public void RunControls_RunningDemo_EnablesPauseAndStopOnly()
    {
        AutoPlayerStatus status = DemoData.Status();

        Assert.False(status.NeedsProcessRestart);
        Assert.Equal(AutoPlayerRunState.Running, status.RunState);
        RunControlAvailability availability = RunControlAvailability.From(
            sessionTrusted: true,
            status);

        Assert.False(availability.CanStart);
        Assert.True(availability.CanPause);
        Assert.False(availability.CanResume);
        Assert.True(availability.CanStop);
    }

    [Fact]
    public void RunControls_RestartRequiredCompletedDemo_DisablesEveryCommand()
    {
        AutoPlayerStatus restartRequired = DemoData.Status(needsProcessRestart: true);

        RunControlAvailability availability = RunControlAvailability.From(
            sessionTrusted: true,
            restartRequired);

        Assert.False(availability.CanStart);
        Assert.False(availability.CanPause);
        Assert.False(availability.CanResume);
        Assert.False(availability.CanStop);
    }

    [Fact]
    public void RunControls_CompletedWithoutRestart_AllowsNewRun()
    {
        AutoPlayerStatus completed = new()
        {
            RunState = AutoPlayerRunState.Completed
        };

        RunControlAvailability availability = RunControlAvailability.From(
            sessionTrusted: true,
            completed);

        Assert.True(availability.CanStart);
        Assert.False(availability.CanPause);
        Assert.False(availability.CanResume);
        Assert.False(availability.CanStop);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RunControls_CheatObservationModeDoesNotBlockStart(
        bool enabled,
        bool used)
    {
        AutoPlayerStatus status = new()
        {
            RunState = AutoPlayerRunState.Standby,
            CheatModeEnabled = enabled,
            CheatUsed = used,
            CheatSessionAuthorized = true
        };

        RunControlAvailability availability = RunControlAvailability.From(sessionTrusted: true, status);

        Assert.True(availability.CanStart);
        Assert.False(availability.CanPause);
        Assert.False(availability.CanResume);
        Assert.False(availability.CanStop);
    }

    [Fact]
    public void RunControls_AuthorizedButUnusedCheatCapability_AllowsNormalStart()
    {
        AutoPlayerStatus status = new()
        {
            RunState = AutoPlayerRunState.Standby,
            CheatSessionAuthorized = true,
            CheatModeEnabled = false,
            CheatUsed = false
        };

        RunControlAvailability availability = RunControlAvailability.From(sessionTrusted: true, status);

        Assert.True(availability.CanStart);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RunControls_PersistentCheatEffectsBlockStart(bool baseGodMode, bool mapSkip)
    {
        AutoPlayerStatus status = new()
        {
            RunState = AutoPlayerRunState.Standby,
            CheatModeEnabled = true,
            BaseGodModeEnabled = baseGodMode,
            MapSkipEnabled = mapSkip
        };

        RunControlAvailability availability = RunControlAvailability.From(sessionTrusted: true, status);

        Assert.False(availability.CanStart);
    }

    [Theory]
    [InlineData("INFO", "信息")]
    [InlineData("WARN", "警告")]
    [InlineData("ERROR", "错误")]
    [InlineData("SAFE", "安全")]
    [InlineData("ACT", "操作")]
    [InlineData("STATE", "状态")]
    [InlineData("GAME", "游戏")]
    [InlineData("CHEAT", "作弊")]
    public void LogCategoryName_MapsConsoleCategoriesToChinese(string category, string expected)
    {
        Assert.Equal(expected, MainForm.LogCategoryName(category));
    }

    [Theory]
    [InlineData("start", "开始命令")]
    [InlineData("pause", "暂停命令")]
    [InlineData("resume", "继续命令")]
    [InlineData("stop", "停止命令")]
    public void ControlCommandName_MapsProtocolCommandsForDisplayOnly(string command, string expected)
    {
        Assert.Equal(expected, MainForm.ControlCommandName(command));
    }

    [Fact]
    public void DemoLogLines_UseChineseConsoleCategories()
    {
        IReadOnlyList<string> lines = DemoData.LogLines();

        Assert.Contains(lines, line => line.Contains("信息", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("安全", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("操作", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line =>
            line.Contains(" INFO ", StringComparison.Ordinal)
            || line.Contains(" SAFE ", StringComparison.Ordinal)
            || line.Contains(" ACT ", StringComparison.Ordinal));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new TargetInvocationException(failure);
    }

    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{element.Name} 没有获得可见尺寸。");
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(
            bounds.Left >= -1
            && bounds.Top >= -1
            && bounds.Right <= root.ActualWidth + 1
            && bounds.Bottom <= root.ActualHeight + 1,
            $"{element.Name} 超出窗口内容范围：{bounds}，根区域 {root.ActualWidth:0.##}x{root.ActualHeight:0.##}。");
    }

    private static void SaveScreenshot(FrameworkElement root, int width, int height)
    {
        System.Windows.DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY));
        System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string directory = Path.Combine(repositoryRoot, "artifacts", "ui", "v0.6.49");
        Directory.CreateDirectory(directory);
        using FileStream stream = new(Path.Combine(directory, $"manager-{width}x{height}.png"), FileMode.Create);
        encoder.Save(stream);
    }
}
