using System.Diagnostics;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ManagerRestarter
{
    public Process Restart(string releaseRoot)
    {
        ProcessStartInfo startInfo = CreateStartInfo(releaseRoot);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Windows did not restart Manager.");
    }

    internal static ProcessStartInfo CreateStartInfo(string releaseRoot)
    {
        string root = ReleasePackageValidator.NormalizeRoot(releaseRoot);
        ReleaseMarker marker = ReleasePackageValidator.ReadMarker(root);
        string managerEntryPoint = ReleasePackageValidator.ResolveEntryPoint(
            root,
            marker.ManagerPath,
            "manager/Loopstructor.AutoPlayer.Manager.exe",
            "Loopstructor.AutoPlayer.Manager",
            "Manager entry point");
        string managerDirectory = Path.GetDirectoryName(managerEntryPoint)
                                  ?? throw new InvalidDataException("Manager entry point has no parent directory.");
        ProcessStartInfo startInfo;
        if (managerEntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo(managerEntryPoint);
        }
        else
        {
            startInfo = new ProcessStartInfo("dotnet");
            startInfo.ArgumentList.Add(managerEntryPoint);
        }

        startInfo.WorkingDirectory = managerDirectory;
        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add("--restarted-after-update");
        return startInfo;
    }
}
