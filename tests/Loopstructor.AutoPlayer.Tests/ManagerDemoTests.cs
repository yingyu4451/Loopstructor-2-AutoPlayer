using System.Drawing;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerDemoTests
{
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
        Assert.Equal(new Size(1280, 720), options.WindowSize);
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
    public void CheatDemo_QueryCatalog_ContainsChineseVehicleLevels()
    {
        ControlResponse response = DemoData.CheatResponse(CheatCommands.QueryCatalog, null);
        JArray vehicles = Assert.IsType<JArray>(response.Data!["vehicles"]);

        Assert.True(response.Success);
        Assert.Contains(vehicles.OfType<JObject>(), item =>
            item.Value<string>("name") == "雷叉" && item.Value<int>("level") == 2);
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
}
