using System.Drawing;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerDemoTests
{
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
    public void DemoStatus_RestartVariant_ExposesRestartGateFromCompletedState()
    {
        AutoPlayerStatus status = DemoData.Status(needsProcessRestart: true);

        Assert.True(status.NeedsProcessRestart);
        Assert.Equal(AutoPlayerRunState.Completed, status.RunState);
        Assert.Equal(AutomationStage.Completed, status.Stage);
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
    [InlineData("INFO", "信息")]
    [InlineData("WARN", "警告")]
    [InlineData("ERROR", "错误")]
    [InlineData("SAFE", "安全")]
    [InlineData("ACT", "操作")]
    [InlineData("STATE", "状态")]
    [InlineData("GAME", "游戏")]
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
