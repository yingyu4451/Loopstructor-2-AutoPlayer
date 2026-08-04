using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.UI;
using System.Windows;
using System.Windows.Threading;

namespace Loopstructor.AutoPlayer.Manager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(args);
        System.Windows.Application application = new()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        return application.Run(new MainForm(options));
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        System.Windows.MessageBox.Show(
            "AutoPlayer Manager 遇到未处理错误。请保留日志并重新启动程序。\n\n" + eventArgs.Exception.Message,
            "Loopstructor 2.AutoPlayer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
        ((System.Windows.Application)sender).Shutdown(1);
    }
}
