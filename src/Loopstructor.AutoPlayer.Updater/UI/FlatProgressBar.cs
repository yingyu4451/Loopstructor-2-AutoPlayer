using System.Drawing;
using System.Windows.Forms;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed class FlatProgressBar : Control
{
    private int _value;
    private bool _failed;
    private bool _completed;

    public FlatProgressBar()
    {
        DoubleBuffered = true;
        Height = 12;
        MinimumSize = new Size(80, 10);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "更新总进度";
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetProgress(int value, bool failed, bool completed)
    {
        int normalized = Math.Clamp(value, 0, 100);
        if (_value == normalized && _failed == failed && _completed == completed) return;
        _value = normalized;
        _failed = failed;
        _completed = completed;
        AccessibleDescription = $"{_value}%";
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Rectangle track = new(0, 1, Width, Math.Max(1, Height - 2));
        using SolidBrush trackBrush = new(UpdaterTheme.Line);
        eventArgs.Graphics.FillRectangle(trackBrush, track);

        int fillWidth = (int)Math.Round(track.Width * (_value / 100d));
        if (fillWidth <= 0) return;
        Color fillColor = _failed
            ? UpdaterTheme.Red
            : _completed ? UpdaterTheme.Blue : UpdaterTheme.Teal;
        using SolidBrush fillBrush = new(fillColor);
        eventArgs.Graphics.FillRectangle(fillBrush, new Rectangle(track.X, track.Y, fillWidth, track.Height));
    }
}
