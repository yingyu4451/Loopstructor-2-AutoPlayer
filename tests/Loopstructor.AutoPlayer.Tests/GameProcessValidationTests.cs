using System.Diagnostics;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GameProcessValidationTests
{
    [Fact]
    public void ValidateGameProcess_AcceptsCurrentProcessAtExactPath()
    {
        using Process process = Process.GetCurrentProcess();
        string executable = process.MainModule!.FileName;

        bool valid = MainForm.ValidateGameProcess(process.Id, executable, out string error);

        Assert.True(valid, error);
        Assert.Empty(error);
    }

    [Fact]
    public void ValidateGameProcess_RejectsMissingOrWrongProcessIdentity()
    {
        Assert.False(MainForm.ValidateGameProcess(0, "missing.exe", out string missingError));
        Assert.Contains("PID", missingError, StringComparison.Ordinal);

        using Process process = Process.GetCurrentProcess();
        string wrongExecutable = Path.Combine(Path.GetTempPath(), "different-game.exe");
        Assert.False(MainForm.ValidateGameProcess(process.Id, wrongExecutable, out string wrongPathError));
        Assert.Contains("不属于", wrongPathError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 42, true)]
    [InlineData(0, 42, true)]
    [InlineData(42, 42, true)]
    [InlineData(41, 42, false)]
    public void ExpectedProcessBinding_RejectsAnotherRunningGame(
        int? expectedProcessId,
        int actualProcessId,
        bool expected)
    {
        Assert.Equal(expected, MainForm.MatchesExpectedGameProcess(expectedProcessId, actualProcessId));
    }

    [Fact]
    public void ExpectedProcessBinding_OnlyRejectsDifferentHelloWhileExpectedGameStillOwnsPid()
    {
        using Process process = Process.GetCurrentProcess();
        string executable = process.MainModule!.FileName;

        Assert.True(MainForm.ShouldRejectHelloProcess(process.Id, process.Id + 1, executable));
        Assert.False(MainForm.ShouldRejectHelloProcess(process.Id, process.Id, executable));
        Assert.False(MainForm.ShouldRejectHelloProcess(
            process.Id,
            process.Id + 1,
            Path.Combine(Path.GetTempPath(), "pid-reused-by-another-process.exe")));
        Assert.False(MainForm.ShouldRejectHelloProcess(int.MaxValue, process.Id, executable));
    }

    [Fact]
    public void BoundProcessIdentity_RequiresMatchingExecutableAndStartTime()
    {
        using Process process = Process.GetCurrentProcess();
        string executable = process.MainModule!.FileName;
        DateTime startTimeUtc = process.StartTime.ToUniversalTime();

        Assert.True(MainForm.TryGetGameProcessStartTimeUtc(process.Id, executable, out DateTime observed));
        Assert.Equal(startTimeUtc, observed);
        Assert.True(MainForm.MatchesBoundGameProcess(process.Id, startTimeUtc, executable));
        Assert.False(MainForm.MatchesBoundGameProcess(process.Id, startTimeUtc.AddTicks(1), executable));
        Assert.False(MainForm.MatchesBoundGameProcess(
            process.Id,
            startTimeUtc,
            Path.Combine(Path.GetTempPath(), "different-game.exe")));
    }

    [Theory]
    [InlineData(null, new int[0], null)]
    [InlineData(null, new[] { 11 }, 11)]
    [InlineData(null, new[] { 11, 12 }, null)]
    [InlineData(12, new[] { 11, 12 }, 12)]
    [InlineData(13, new[] { 11, 12 }, null)]
    public void RunningProcessSelection_PreservesBindingAndRejectsAmbiguousDiscovery(
        int? preferredProcessId,
        int[] candidates,
        int? expected)
    {
        Assert.Equal(expected, MainForm.SelectRunningGameProcess(preferredProcessId, candidates));
    }
}
