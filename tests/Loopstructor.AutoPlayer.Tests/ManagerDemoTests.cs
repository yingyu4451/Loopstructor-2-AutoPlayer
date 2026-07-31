using System.Drawing;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerDemoTests
{
    [Theory]
    [InlineData("0.1.6", "AutoPlayer 版本 v0.1.6")]
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
}
