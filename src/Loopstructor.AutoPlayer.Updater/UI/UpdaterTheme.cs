using System.Windows.Controls;
using System.Windows.Media;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal static class UpdaterTheme
{
    public static readonly SolidColorBrush Canvas = Frozen("#0A0907");
    public static readonly SolidColorBrush Surface = Frozen("#15120E");
    public static readonly SolidColorBrush SurfaceRaised = Frozen("#21180F");
    public static readonly SolidColorBrush Ink = Frozen("#F1DFC0");
    public static readonly SolidColorBrush Muted = Frozen("#A58E6E");
    public static readonly SolidColorBrush Line = Frozen("#664326");
    public static readonly SolidColorBrush Copper = Frozen("#8D5A2C");
    public static readonly SolidColorBrush Brass = Frozen("#C18A46");
    public static readonly SolidColorBrush Gold = Frozen("#F0B33A");
    public static readonly SolidColorBrush SignalGreen = Frozen("#79D53B");
    public static readonly SolidColorBrush SignalGreenDark = Frozen("#4D9228");
    public static readonly SolidColorBrush Amber = Frozen("#E89A32");
    public static readonly SolidColorBrush Red = Frozen("#D94B34");
    public static readonly SolidColorBrush Blue = Frozen("#3C8FC5");
    public static readonly SolidColorBrush Console = Frozen("#080806");
    public static readonly SolidColorBrush ConsoleText = Frozen("#D8CBB5");
    public static readonly SolidColorBrush ActiveSurface = Frozen("#10170D");
    public static readonly SolidColorBrush AlertSurface = Frozen("#25140C");
    public static readonly SolidColorBrush Disabled = Frozen("#5D5142");

    public static void SetCommandButtonColor(Button button, Brush color)
    {
        button.Background = color;
        button.BorderBrush = color == Disabled ? Line : Gold;
    }

    private static SolidColorBrush Frozen(string value)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
