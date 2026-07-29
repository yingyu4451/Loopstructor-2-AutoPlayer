using System.Diagnostics;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ManagerRestarter
{
    public Process Restart(string releaseRoot)
    {
        string managerDirectory = Path.Combine(Path.GetFullPath(releaseRoot), "manager");
        string executable = Path.Combine(managerDirectory, "Loopstructor.AutoPlayer.Manager.exe");
        string assembly = Path.Combine(managerDirectory, "Loopstructor.AutoPlayer.Manager.dll");
        ProcessStartInfo startInfo;
        if (File.Exists(executable))
        {
            startInfo = new ProcessStartInfo(executable);
        }
        else if (File.Exists(assembly))
        {
            startInfo = new ProcessStartInfo("dotnet");
            startInfo.ArgumentList.Add(assembly);
        }
        else
        {
            throw new FileNotFoundException("Updated Manager executable was not found.", executable);
        }

        startInfo.WorkingDirectory = managerDirectory;
        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add("--restarted-after-update");
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Windows did not restart Manager.");
    }
}
