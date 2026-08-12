using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Loopstructor.AutoPlayer.Manager.UI.Controls;

[TemplatePart(Name = LogoPartName, Type = typeof(FrameworkElement))]
internal sealed class MechanicalShell : ContentControl
{
    private const string LogoPartName = "PART_LogoButton";

    private FrameworkElement? _logoButton;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MechanicalShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LogoSourceProperty = DependencyProperty.Register(
        nameof(LogoSource),
        typeof(ImageSource),
        typeof(MechanicalShell),
        new PropertyMetadata(null));

    public static readonly DependencyProperty BrandTextProperty = DependencyProperty.Register(
        nameof(BrandText),
        typeof(string),
        typeof(MechanicalShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(MechanicalShell),
        new PropertyMetadata(Brushes.Transparent));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ImageSource? LogoSource
    {
        get => (ImageSource?)GetValue(LogoSourceProperty);
        set => SetValue(LogoSourceProperty, value);
    }

    public string BrandText
    {
        get => (string)GetValue(BrandTextProperty);
        set => SetValue(BrandTextProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public override void OnApplyTemplate()
    {
        if (_logoButton != null)
        {
            _logoButton.MouseLeftButtonUp -= LogoButtonOnMouseLeftButtonUp;
            _logoButton.MouseRightButtonUp -= TitleBarOnMouseRightButtonUp;
        }

        base.OnApplyTemplate();

        _logoButton = GetTemplateChild(LogoPartName) as FrameworkElement;
        if (_logoButton != null)
        {
            _logoButton.MouseLeftButtonUp += LogoButtonOnMouseLeftButtonUp;
            _logoButton.MouseRightButtonUp += TitleBarOnMouseRightButtonUp;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseRightButtonUp(eventArgs);
        if (!eventArgs.Handled && eventArgs.GetPosition(this).Y <= 72)
        {
            ShowSystemMenu(eventArgs);
        }
    }

    private void LogoButtonOnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs) =>
        ShowSystemMenu(eventArgs);

    private void TitleBarOnMouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs) =>
        ShowSystemMenu(eventArgs);

    private void ShowSystemMenu(MouseButtonEventArgs eventArgs)
    {
        Window? window = Window.GetWindow(this);
        if (window == null) return;

        Point screenPoint = window.PointToScreen(eventArgs.GetPosition(window));
        SystemCommands.ShowSystemMenu(window, screenPoint);
        eventArgs.Handled = true;
    }
}
