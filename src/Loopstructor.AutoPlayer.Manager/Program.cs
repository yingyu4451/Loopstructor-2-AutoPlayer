using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Loopstructor.AutoPlayer.Manager.UI;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace Loopstructor.AutoPlayer.Manager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using ManagerSingleInstance singleInstance = ManagerSingleInstance.Create();
        if (!singleInstance.IsPrimary)
        {
            singleInstance.NotifyPrimary();
            return 0;
        }

        ManagerLaunchOptions options = ManagerLaunchOptions.Parse(args);
        if (options.RestartedAfterUpdate)
        {
            LegacyUpdateArtifactCleaner.CleanupAfterUpdate(DistributionLayout.Locate().Root);
        }

        System.Windows.Application application = new()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        MainForm form = new(options);
        singleInstance.StartListening(() => form.Dispatcher.BeginInvoke(() => ActivateExisting(form)));
        return application.Run(form);
    }

    private static void ActivateExisting(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate();
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

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
