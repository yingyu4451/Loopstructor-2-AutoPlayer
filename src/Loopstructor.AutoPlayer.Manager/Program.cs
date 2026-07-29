using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;

namespace Loopstructor.AutoPlayer.Manager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(args);
        Application.Run(new MainForm(options));
    }
}
