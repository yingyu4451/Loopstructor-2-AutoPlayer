using System.Diagnostics;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UpdaterCleanupTests
{
    [Fact]
    public async Task Cleanup_WaitsForRequestedProcessBeforeRestartingManager()
    {
        string targetRoot = Path.Combine(
            Path.GetTempPath(),
            "Loopstructor-UpdaterCleanupTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetRoot);
        using Process blocker = Process.Start(new ProcessStartInfo("ping.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-n",
                "3",
                "-w",
                "1000",
                "127.0.0.1"
            }
        }) ?? throw new InvalidOperationException("Unable to start the cleanup blocker process.");

        try
        {
            Assert.False(blocker.HasExited);
            UpdateCommandOptions options = UpdateCommandOptions.Parse(new[]
            {
                "cleanup",
                "--target", targetRoot,
                "--current-version", "0.6.72",
                "--wait-pid", blocker.Id.ToString(),
                "--restart-manager",
                "--wait-timeout-seconds", "10"
            });
            bool restarted = false;

            UpdaterResult result = await Loopstructor.AutoPlayer.Updater.Program.ExecuteCleanupAsync(
                options,
                releaseRoot =>
                {
                    Assert.True(blocker.HasExited);
                    Assert.Equal(targetRoot, releaseRoot);
                    restarted = true;
                });

            Assert.True(result.Success, result.Message);
            Assert.True(restarted);
        }
        finally
        {
            if (!blocker.HasExited) blocker.Kill(entireProcessTree: true);
            await blocker.WaitForExitAsync();
            Directory.Delete(targetRoot, recursive: true);
        }
    }
}
