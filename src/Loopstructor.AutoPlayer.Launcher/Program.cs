using System.Diagnostics;
using System.Runtime.InteropServices;

return RootLauncher.Run(args);

internal static class RootLauncher
{
    internal const string ManagerDirectoryName = "manager";
    internal const string ManagerExecutableName = "Loopstructor-2-QA-Tool.exe";

    internal static int Run(IReadOnlyList<string> arguments)
    {
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(AppContext.BaseDirectory, arguments);
            if (!File.Exists(startInfo.FileName))
            {
                throw new FileNotFoundException("发布包内的 Manager 程序缺失。", startInfo.FileName);
            }

            return Process.Start(startInfo) == null
                ? ShowError("Windows 未能启动发布包内的 Manager。")
                : 0;
        }
        catch (Exception exception)
        {
            return ShowError("Loopstructor 2 QA Tool 无法启动。\n\n详细信息：" + exception.Message);
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
        MessageBoxW(IntPtr.Zero, message, "Loopstructor 2 QA Tool", 0x10);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
