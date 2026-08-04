using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed class FlatProgressBar : FrameworkElement
{
    private int _value;
    private bool _failed;
    private bool _completed;

    public FlatProgressBar()
    {
        MinWidth = 120;
        MinHeight = 16;
        AutomationProperties.SetName(this, "更新总进度");
    }

    public void SetProgress(int value, bool failed, bool completed)
    {
        int normalized = Math.Clamp(value, 0, 100);
        if (_value == normalized && _failed == failed && _completed == completed) return;
        _value = normalized;
        _failed = failed;
        _completed = completed;
        AutomationProperties.SetHelpText(this, $"{_value}%");
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect outer = new(0d, 0d, Math.Max(0d, ActualWidth), Math.Max(0d, ActualHeight));
        drawingContext.DrawRectangle(UpdaterTheme.Canvas, new Pen(UpdaterTheme.Copper, 2d), outer);
        Rect track = new(4d, 4d, Math.Max(0d, ActualWidth - 8d), Math.Max(0d, ActualHeight - 8d));
        drawingContext.DrawRectangle(UpdaterTheme.SurfaceRaised, null, track);

        double fillWidth = track.Width * (_value / 100d);
        if (fillWidth <= 0d) return;
        Brush fill = _failed
            ? UpdaterTheme.Red
            : _completed ? UpdaterTheme.SignalGreen : UpdaterTheme.Gold;
        Rect completed = new(track.X, track.Y, fillWidth, track.Height);
        drawingContext.DrawRectangle(fill, null, completed);

        for (double x = track.X + 12d; x < completed.Right; x += 16d)
        {
            drawingContext.DrawLine(
                new Pen(UpdaterTheme.Canvas, 1d),
                new Point(x, track.Top),
                new Point(x - 5d, track.Bottom));
        }
    }
}
