using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public void InterpretCheckResult_NonzeroExitRejectsSuccessfulJson()
    {
        string output = JsonConvert.SerializeObject(new ManagerUpdateStatus
        {
            Success = true,
            UpdateAvailable = true,
            CurrentVersion = "1.0.0",
            LatestVersion = "2.0.0",
            Message = "AutoPlayer 2.0.0 is available."
        });

        ManagerUpdateStatus result = UpdateCoordinator.InterpretCheckResult(
            exitCode: 23,
            output,
            error: string.Empty,
            currentVersion: "1.0.0");

        Assert.False(result.Success);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.Contains("exited with code 23", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterpretCheckResult_ZeroExitAcceptsSuccessfulJson()
    {
        string output = JsonConvert.SerializeObject(new ManagerUpdateStatus
        {
            Success = true,
            UpdateAvailable = true,
            CurrentVersion = "1.0.0",
            LatestVersion = "2.0.0",
            Message = "AutoPlayer 2.0.0 is available."
        });

        ManagerUpdateStatus result = UpdateCoordinator.InterpretCheckResult(
            exitCode: 0,
            output,
            error: string.Empty,
            currentVersion: "1.0.0");

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersion);
    }
}
