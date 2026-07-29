using System.Drawing.Drawing2D;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal static class Theme
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
            Height = 34,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Body(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Lighten(color, 0.08f);
        button.FlatAppearance.MouseDownBackColor = Darken(color, 0.08f);
        return button;
    }

    public static Label Caption(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Muted,
        Font = Body(8.5f),
        Margin = new Padding(0, 0, 0, 5)
    };

    public static Label Value(string text = "-") => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Text = text,
        ForeColor = Ink,
        Font = Data(8.5f),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Color Lighten(Color color, float amount) => Blend(color, Color.White, amount);
    private static Color Darken(Color color, float amount) => Blend(color, Color.Black, amount);

    private static Color Blend(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            (int)(from.R + ((to.R - from.R) * amount)),
            (int)(from.G + ((to.G - from.G) * amount)),
            (int)(from.B + ((to.B - from.B) * amount)));
    }
}

internal sealed class SectionPanel : Panel
{
    public SectionPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Padding = new Padding(16);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using Pen border = new(Theme.Line);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class ConnectionBadge : Control
{
    private Color _stateColor = Theme.Muted;
    private string _stateText = "未连接";

    public ConnectionBadge()
    {
        DoubleBuffered = true;
        Size = new Size(132, 30);
        Font = Theme.Body(8.5f, FontStyle.Bold);
        ForeColor = Color.White;
    }

    public void SetState(string text, Color color)
    {
        _stateText = text;
        _stateColor = color;
        Invalidate();
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
            new Rectangle(27, 0, Width - 31, Height),
            ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
