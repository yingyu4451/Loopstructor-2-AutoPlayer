using System.Diagnostics;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RootLauncherTests
{
    [Fact]
    public void CreateStartInfo_TargetsBundledManagerAndForwardsArguments()
    {
        string releaseRoot = Path.Combine(Path.GetTempPath(), "Loopstructor AutoPlayer Release");
        string[] arguments = { "--restarted-after-update", "value with spaces" };

        ProcessStartInfo startInfo = RootLauncher.CreateStartInfo(releaseRoot, arguments);

        string managerDirectory = Path.Combine(Path.GetFullPath(releaseRoot), "manager");
        Assert.Equal(Path.Combine(managerDirectory, "Loopstructor.AutoPlayer.Manager.exe"), startInfo.FileName);
        Assert.Equal(managerDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(arguments, startInfo.ArgumentList);
    }
}
