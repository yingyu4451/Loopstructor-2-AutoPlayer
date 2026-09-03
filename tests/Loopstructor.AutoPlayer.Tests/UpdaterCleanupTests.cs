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
        using Process blocker = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Sleep -Milliseconds 400"
            }
        }) ?? throw new InvalidOperationException("Unable to start the cleanup blocker process.");

        try
        {
            UpdateCommandOptions options = UpdateCommandOptions.Parse(new[]
            {
                "cleanup",
                "--target", targetRoot,
                "--current-version", "0.6.65",
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
