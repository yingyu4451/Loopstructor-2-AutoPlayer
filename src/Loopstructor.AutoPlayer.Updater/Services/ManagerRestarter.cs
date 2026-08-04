using System.Diagnostics;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ManagerRestarter
{
    public Process Restart(string releaseRoot)
    {
        ProcessStartInfo startInfo = CreateStartInfo(releaseRoot);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Windows 未能重新启动 Manager。");
    }

    internal static ProcessStartInfo CreateStartInfo(string releaseRoot)
    {
        string root = ReleasePackageValidator.NormalizeRoot(releaseRoot);
        ReleaseMarker marker = ReleasePackageValidator.ReadMarker(root);
        string managerEntryPoint = ReleasePackageValidator.ResolveRequiredEntryPoint(
            root,
            marker.ManagerPath,
            ReleasePackageValidator.RequiredManagerEntryPoint,
            "Manager 入口");
        string managerDirectory = Path.GetDirectoryName(managerEntryPoint)
                                  ?? throw new InvalidDataException("Manager 入口没有父目录。");
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
