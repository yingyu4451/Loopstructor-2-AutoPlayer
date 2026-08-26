using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Manager.UI;
using Loopstructor.AutoPlayer.Manager.UI.Controls;
using Loopstructor.AutoPlayer.Updater.UI;

namespace Loopstructor.AutoPlayer.Tests;

[Collection("WPF UI")]
public sealed class CustomTitleBarWpfTests
{
    [Fact]
    public void CheatTitleBar_UsesCurrentWindowCommandsAndDistinctBranding()
    {
        RunSta(() =>
        {
            Window other = new()
            {
                Width = 320,
                Height = 240,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            CheatForm form = new((command, payload) =>
                Task.FromResult<Loopstructor.AutoPlayer.Core.ControlResponse?>(DemoData.CheatResponse(command, payload)))
            {
                Width = 980,
                Height = 680,
                ShowInTaskbar = false
            };

            try
            {
                other.Show();
                form.Show();
                PumpDispatcher();

                WindowChrome chrome = Assert.IsType<WindowChrome>(WindowChrome.GetWindowChrome(form));
                Assert.Equal(72d, chrome.CaptionHeight);
                Assert.Equal(new Thickness(7d), chrome.ResizeBorderThickness);
                Assert.Equal(WindowStyle.None, form.WindowStyle);
                Assert.False(form.AllowsTransparency);
                Assert.NotNull(form.Icon);

                MechanicalShell shell = Assert.IsType<MechanicalShell>(form.FindName("Shell"));
                Assert.Equal("CHEAT TOOL", shell.BrandText);
                Assert.NotNull(shell.LogoSource);
                Assert.Null(form.FindName("BottomFasteners"));

                MechanicalWindowButton[] buttons = VisualDescendants<MechanicalWindowButton>(shell).ToArray();
                Assert.Equal(3, buttons.Length);
                Assert.All(buttons, button => Assert.Equal(button.ActualWidth, button.ActualHeight, precision: 3));

                MechanicalWindowButton minimize = Assert.Single(buttons, button => button.Kind == MechanicalWindowButtonKind.Minimize);
                MechanicalWindowButton maximize = Assert.Single(buttons, button => button.Kind == MechanicalWindowButtonKind.MaximizeRestore);
                MechanicalWindowButton close = Assert.Single(buttons, button => button.Kind == MechanicalWindowButtonKind.Close);
                Assert.Equal("最小化", AutomationProperties.GetName(minimize));
                Assert.Equal("最大化", AutomationProperties.GetName(maximize));
                Assert.Equal("关闭", AutomationProperties.GetName(close));

                Invoke(minimize);
                PumpDispatcher();
                Assert.Equal(WindowState.Minimized, form.WindowState);
                Assert.Equal(WindowState.Normal, other.WindowState);

                SystemCommands.RestoreWindow(form);
                Invoke(maximize);
                PumpDispatcher();
                Assert.Equal(WindowState.Maximized, form.WindowState);
                Assert.True(maximize.IsRestoreAction);
                Assert.Equal("还原", AutomationProperties.GetName(maximize));

                Invoke(maximize);
                PumpDispatcher();
                Assert.Equal(WindowState.Normal, form.WindowState);
                Assert.False(maximize.IsRestoreAction);
            }
            finally
            {
                form.Close();
                other.Close();
            }
        });
    }

    [Fact]
    public void UpdaterTitleBar_UsesCustomChromeAndEmbeddedManagerBrand()
    {
        RunSta(() =>
        {
            UpdateForm form = UpdateForm.CreateDemo("0.5.4", "0.5.5", applySavedUiScale: false);
            form.Width = 720;
            form.Height = 600;
            form.ShowInTaskbar = false;

            try
            {
                form.Show();
                PumpDispatcher();

                WindowChrome chrome = Assert.IsType<WindowChrome>(WindowChrome.GetWindowChrome(form));
                Assert.Equal(46d, chrome.CaptionHeight);
                Assert.Equal(WindowStyle.None, form.WindowStyle);
                Assert.False(form.AllowsTransparency);
                Assert.NotNull(form.Icon);

                FrameworkElement logo = Assert.IsAssignableFrom<FrameworkElement>(form.FindName("WindowMenuButton"));
                FrameworkElement minimize = Assert.IsAssignableFrom<FrameworkElement>(
                    VisualDescendants<FrameworkElement>(form)
                        .Single(element => AutomationProperties.GetName(element) == "最小化"));
                FrameworkElement maximize = Assert.IsAssignableFrom<FrameworkElement>(form.FindName("MaximizeButton"));

                Assert.Equal("打开窗口菜单", AutomationProperties.GetName(logo));
                Assert.Equal("最大化", AutomationProperties.GetName(maximize));
                Assert.Equal(minimize.ActualWidth, minimize.ActualHeight, precision: 3);
                Assert.Equal(maximize.ActualWidth, maximize.ActualHeight, precision: 3);

                Button action = Assert.IsType<Button>(form.FindName("ActionButton"));
                action.IsEnabled = false;
                action.ApplyTemplate();
                Border face = Assert.IsType<Border>(action.Template.FindName("ButtonFace", action));
                SolidColorBrush disabledBackground = Assert.IsType<SolidColorBrush>(face.Background);
                Assert.Equal(Color.FromRgb(31, 26, 21), disabledBackground.Color);
            }
            finally
            {
                form.Close();
            }
        });
    }

    [Fact]
    public void UpdateConfirmation_UsesMechanicalModalActionsAndVersionPlaques()
    {
        RunSta(() =>
        {
            UpdateConfirmationDialog dialog = new("0.6.5", "0.6.6")
            {
                ShowInTaskbar = false
            };
            try
            {
                dialog.Show();
                PumpDispatcher();

                Assert.Equal(WindowStyle.None, dialog.WindowStyle);
                Assert.False(dialog.AllowsTransparency);
                WindowChrome chrome = Assert.IsType<WindowChrome>(WindowChrome.GetWindowChrome(dialog));
                Assert.Equal(72d, chrome.CaptionHeight);
                MechanicalShell shell = Assert.IsType<MechanicalShell>(dialog.Content);
                Assert.Equal("UPDATE READY", shell.BrandText);
                Assert.NotNull(shell.LogoSource);
                TextBlock current = Assert.IsType<TextBlock>(dialog.FindName("CurrentVersionText"));
                TextBlock latest = Assert.IsType<TextBlock>(dialog.FindName("LatestVersionText"));
                Assert.Equal("v0.6.5", current.Text);
                Assert.Equal("v0.6.6", latest.Text);
                Button apply = Assert.IsType<Button>(dialog.FindName("ApplyButton"));
                Button later = Assert.IsType<Button>(dialog.FindName("LaterButton"));
                Assert.True(apply.IsDefault);
                Assert.True(later.IsCancel);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void Invoke(System.Windows.Controls.Button button)
    {
        ButtonAutomationPeer peer = new(button);
        IInvokeProvider provider = Assert.IsAssignableFrom<IInvokeProvider>(
            peer.GetPattern(PatternInterface.Invoke));
        provider.Invoke();
        PumpDispatcher();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF 标题栏测试超时。");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
