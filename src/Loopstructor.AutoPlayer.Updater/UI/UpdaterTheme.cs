using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal static class UpdaterTheme
{
    public static readonly Color Canvas = Color.FromArgb(237, 241, 243);
    public static readonly Color Surface = Color.FromArgb(252, 253, 253);
    public static readonly Color Ink = Color.FromArgb(32, 38, 43);
    public static readonly Color Muted = Color.FromArgb(99, 111, 119);
    public static readonly Color Line = Color.FromArgb(205, 214, 219);
    public static readonly Color Teal = Color.FromArgb(20, 125, 126);
    public static readonly Color TealDark = Color.FromArgb(14, 94, 96);
    public static readonly Color Amber = Color.FromArgb(216, 155, 43);
    public static readonly Color Red = Color.FromArgb(200, 73, 73);
    public static readonly Color Blue = Color.FromArgb(45, 106, 160);
    public static readonly Color Console = Color.FromArgb(27, 31, 34);
    public static readonly Color ConsoleText = Color.FromArgb(220, 227, 230);
    public static readonly Color ActiveSurface = Color.FromArgb(230, 242, 241);
    public static readonly Color AlertSurface = Color.FromArgb(252, 237, 225);

    public static Font Display(float size, FontStyle style = FontStyle.Regular) =>
        new("Bahnschrift", size, style, GraphicsUnit.Point);

    public static Font Body(float size, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static Font Data(float size, FontStyle style = FontStyle.Regular) =>
        new("Consolas", size, style, GraphicsUnit.Point);

    public static Button CommandButton(string text, Color color, int width = 96)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 32,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Body(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        SetCommandButtonColor(button, color);
        return button;
    }

    public static void SetCommandButtonColor(Button button, Color color)
    {
        button.BackColor = color;
        button.FlatAppearance.MouseOverBackColor = Blend(color, Color.White, 0.08f);
        button.FlatAppearance.MouseDownBackColor = Blend(color, Color.Black, 0.08f);
    }

    public static Label Caption(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Muted,
        Font = Body(8.5f),
        Margin = Padding.Empty
    };

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + ((to.R - from.R) * amount)),
        (int)(from.G + ((to.G - from.G) * amount)),
        (int)(from.B + ((to.B - from.B) * amount)));
}

internal sealed class UpdaterSectionPanel : Panel
{
    public UpdaterSectionPanel()
    {
        DoubleBuffered = true;
        BackColor = UpdaterTheme.Surface;
        Padding = new Padding(16);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 0 || Height <= 0) return;
        using Pen border = new(UpdaterTheme.Line);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class UpdateMetricDisplay : Control
{
    private readonly Font _captionFont = UpdaterTheme.Body(8.5f);
    private readonly Font _valueFont = UpdaterTheme.Data(9f, FontStyle.Bold);
    private readonly string _caption;
    private string _value = "-";

    public UpdateMetricDisplay(string caption)
    {
        _caption = caption;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        BackColor = UpdaterTheme.Surface;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = caption;
    }

    public void SetValue(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? "-" : value;
        if (string.Equals(_value, normalized, StringComparison.Ordinal)) return;
        _value = normalized;
        AccessibleDescription = normalized;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        int contentWidth = Math.Max(0, ClientSize.Width - Padding.Horizontal);
        int contentHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
        Rectangle captionBounds = new(Padding.Left, Padding.Top, contentWidth, Math.Min(20, contentHeight));
        Rectangle valueBounds = new(
            Padding.Left,
            Padding.Top + captionBounds.Height,
            contentWidth,
            Math.Max(0, contentHeight - captionBounds.Height));
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _caption,
            _captionFont,
            captionBounds,
            UpdaterTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _value,
            _valueFont,
            valueBounds,
            UpdaterTheme.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captionFont.Dispose();
            _valueFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class UpdateStatusBadge : Control
{
    private Color _stateColor = UpdaterTheme.Muted;
    private string _stateText = "准备中";

    public UpdateStatusBadge()
    {
        DoubleBuffered = true;
        Size = new Size(132, 30);
        Font = UpdaterTheme.Body(8.5f, FontStyle.Bold);
        ForeColor = Color.White;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = "更新状态";
    }

    public void SetState(string text, Color color)
    {
        _stateText = text;
        _stateColor = color;
        AccessibleDescription = text;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush background = new(Color.FromArgb(48, 55, 60));
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        using SolidBrush indicator = new(_stateColor);
        eventArgs.Graphics.FillRectangle(indicator, 10, 10, 9, 9);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _stateText,
            Font,
            new Rectangle(27, 0, Math.Max(0, Width - 31), Height),
            ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
