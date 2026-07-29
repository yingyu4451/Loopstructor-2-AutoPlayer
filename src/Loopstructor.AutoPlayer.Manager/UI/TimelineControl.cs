using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed class TimelineControl : Control
{
    private const int RowHeight = 64;
    private IReadOnlyList<TimelineEvent> _events = Array.Empty<TimelineEvent>();

    public TimelineControl()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Font = Theme.Body(9f);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetEvents(IReadOnlyList<TimelineEvent>? events)
    {
        _events = events ?? Array.Empty<TimelineEvent>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Graphics graphics = eventArgs.Graphics;
        graphics.Clear(BackColor);
        int capacity = Math.Max(1, Height / RowHeight);
        TimelineEvent[] visible = _events.TakeLast(capacity).ToArray();
        if (visible.Length == 0)
        {
            TextRenderer.DrawText(
                graphics,
                "等待自动游玩事件",
                Theme.Body(10f),
                new Rectangle(20, 28, Width - 40, 28),
                Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }

        int railX = 27;
        int firstY = 30;
        int lastY = firstY + ((visible.Length - 1) * RowHeight);
        using Pen rail = new(Theme.Line, 4f);
        graphics.DrawLine(rail, railX, firstY, railX, lastY);

        for (int index = 0; index < visible.Length; index++)
        {
            TimelineEvent item = visible[index];
            int centerY = firstY + (index * RowHeight);
            Color markerColor = ColorForKind(item.Kind);
            using SolidBrush outer = new(Theme.Surface);
            using SolidBrush marker = new(markerColor);
            graphics.FillEllipse(outer, railX - 9, centerY - 9, 18, 18);
            graphics.FillEllipse(marker, railX - 6, centerY - 6, 12, 12);

            string time = item.TimestampUtc == default
                ? "--:--:--"
                : item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            Rectangle heading = new(48, centerY - 18, Width - 64, 20);
            Rectangle detail = new(48, centerY + 3, Width - 64, 22);
            TextRenderer.DrawText(
                graphics,
                $"{time}  {StageName(item.Stage)}",
                Theme.Data(8.5f, FontStyle.Bold),
                heading,
                markerColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(
                graphics,
                item.Message,
                Font,
                detail,
                Theme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static Color ColorForKind(string? kind)
    {
        return kind?.ToLowerInvariant() switch
        {
            "error" or "fault" => Theme.Red,
            "warning" => Theme.Amber,
            "complete" => Theme.Blue,
            "command" or "action" => Theme.Teal,
            _ => Theme.Muted
        };
    }

    internal static string StageName(AutomationStage stage) => stage switch
    {
        AutomationStage.WaitingForGame => "等待游戏",
        AutomationStage.FrontEnd => "主菜单",
        AutomationStage.RandomSelection => "随机模式选择",
        AutomationStage.InitializingRun => "初始化对局",
        AutomationStage.PreparingDefense => "准备防线",
        AutomationStage.ManagingRewards => "处理奖励",
        AutomationStage.ManagingEvent => "处理事件",
        AutomationStage.ManagingShop => "处理商店",
        AutomationStage.SelectingRoute => "选择路线",
        AutomationStage.StartingWave => "启动波次",
        AutomationStage.Battle => "战斗",
        AutomationStage.Completed => "完成",
        AutomationStage.Recovery => "恢复",
        _ => stage.ToString()
    };
}
