using System.Diagnostics;
using System.Runtime.InteropServices;

return RootLauncher.Run(args);

internal static class RootLauncher
{
    internal const string ManagerDirectoryName = "manager";
    internal const string ManagerExecutableName = "Loopstructor.AutoPlayer.Manager.exe";

    internal static int Run(IReadOnlyList<string> arguments)
    {
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(AppContext.BaseDirectory, arguments);
            if (!File.Exists(startInfo.FileName))
            {
                throw new FileNotFoundException("The bundled Manager executable is missing.", startInfo.FileName);
            }

            return Process.Start(startInfo) == null
                ? ShowError("Windows did not start the bundled Manager.")
                : 0;
        }
        catch (Exception exception)
        {
            return ShowError("Loopstructor AutoPlayer could not start.\n\n" + exception.Message);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string releaseRoot, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseRoot);
        ArgumentNullException.ThrowIfNull(arguments);

        string managerDirectory = Path.Combine(Path.GetFullPath(releaseRoot), ManagerDirectoryName);
        ProcessStartInfo startInfo = new(Path.Combine(managerDirectory, ManagerExecutableName))
        {
            WorkingDirectory = managerDirectory,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static int ShowError(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "Loopstructor AutoPlayer", 0x10);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
