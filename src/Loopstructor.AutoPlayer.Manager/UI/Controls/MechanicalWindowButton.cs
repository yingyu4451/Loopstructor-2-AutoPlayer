using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shell;

namespace Loopstructor.AutoPlayer.Manager.UI.Controls;

internal enum MechanicalWindowButtonKind
{
    Minimize,
    MaximizeRestore,
    Close
}

internal sealed class MechanicalWindowButton : Button
{
    private static readonly DependencyPropertyKey IsRestoreActionPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsRestoreAction),
            typeof(bool),
            typeof(MechanicalWindowButton),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsRestoreActionProperty =
        IsRestoreActionPropertyKey.DependencyProperty;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(MechanicalWindowButtonKind),
        typeof(MechanicalWindowButton),
        new PropertyMetadata(MechanicalWindowButtonKind.Minimize, KindPropertyChanged));

    private Window? _hostWindow;

    public MechanicalWindowButtonKind Kind
    {
        get => (MechanicalWindowButtonKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsRestoreAction => (bool)GetValue(IsRestoreActionProperty);

    public MechanicalWindowButton()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateAccessibleText();
    }

    protected override void OnClick()
    {
        base.OnClick();

        Window? window = Window.GetWindow(this);
        if (window == null) return;

        switch (Kind)
        {
            case MechanicalWindowButtonKind.Minimize:
                SystemCommands.MinimizeWindow(window);
                break;
            case MechanicalWindowButtonKind.MaximizeRestore:
                if (window.WindowState == WindowState.Maximized)
                {
                    SystemCommands.RestoreWindow(window);
                }
                else
                {
                    SystemCommands.MaximizeWindow(window);
                }
                break;
            case MechanicalWindowButtonKind.Close:
                SystemCommands.CloseWindow(window);
                break;
        }
    }

    private static void KindPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((MechanicalWindowButton)dependencyObject).UpdateAccessibleText();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachToHostWindow(Window.GetWindow(this));
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachToHostWindow(null);
    }

    private void AttachToHostWindow(Window? window)
    {
        if (ReferenceEquals(_hostWindow, window)) return;

        if (_hostWindow != null)
        {
            _hostWindow.StateChanged -= HostWindowOnStateChanged;
        }

        _hostWindow = window;
        if (_hostWindow != null)
        {
            _hostWindow.StateChanged += HostWindowOnStateChanged;
        }

        UpdateWindowState();
    }

    private void HostWindowOnStateChanged(object? sender, EventArgs eventArgs) => UpdateWindowState();

    private void UpdateWindowState()
    {
        SetValue(IsRestoreActionPropertyKey, _hostWindow?.WindowState == WindowState.Maximized);
        UpdateAccessibleText();
    }

    private void UpdateAccessibleText()
    {
        string text = Kind switch
        {
            MechanicalWindowButtonKind.Minimize => "最小化",
            MechanicalWindowButtonKind.MaximizeRestore when IsRestoreAction => "还原",
            MechanicalWindowButtonKind.MaximizeRestore => "最大化",
            MechanicalWindowButtonKind.Close => "关闭",
            _ => "窗口命令"
        };

        ToolTip = text;
        AutomationProperties.SetName(this, text);
    }
}
