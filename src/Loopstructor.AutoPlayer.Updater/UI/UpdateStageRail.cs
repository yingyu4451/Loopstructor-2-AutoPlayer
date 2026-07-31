using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed class UpdateStageRail : Control
{
    private static readonly string[] Labels = { "准备", "下载", "校验", "安装", "重启" };

    private int _activeIndex;
    private bool _failed;
    private bool _warning;
    private bool _completed;

    public UpdateStageRail()
    {
        DoubleBuffered = true;
        BackColor = UpdaterTheme.Surface;
        Font = UpdaterTheme.Body(8.5f, FontStyle.Bold);
        MinimumSize = new Size(300, 48);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "更新阶段";
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetStage(UpdateProgressStage stage, bool failed, bool warning = false)
    {
        _activeIndex = PhaseIndex(stage);
        _completed = stage == UpdateProgressStage.Completed && !failed && !warning;
        _failed = failed;
        _warning = warning;
        AccessibleDescription = failed
            ? $"{Labels[_activeIndex]}阶段失败"
            : warning ? $"{Labels[_activeIndex]}阶段需要处理"
            : _completed ? "全部阶段已完成" : $"当前处于{Labels[_activeIndex]}阶段";
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Graphics graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        const int sidePadding = 36;
        const int railY = 17;
        int usableWidth = Math.Max(1, Width - (sidePadding * 2));
        float step = usableWidth / (float)(Labels.Length - 1);

        for (int index = 0; index < Labels.Length - 1; index++)
        {
            float startX = sidePadding + (index * step);
            float endX = sidePadding + ((index + 1) * step);
            Color segmentColor = _completed || index < _activeIndex
                ? UpdaterTheme.Blue
                : UpdaterTheme.Line;
            using Pen segment = new(segmentColor, 4f);
            graphics.DrawLine(segment, startX, railY, endX, railY);
        }

        for (int index = 0; index < Labels.Length; index++)
        {
            int centerX = (int)Math.Round(sidePadding + (index * step));
            Color nodeColor = NodeColor(index);
            using SolidBrush outer = new(UpdaterTheme.Surface);
            using SolidBrush node = new(nodeColor);
            graphics.FillEllipse(outer, centerX - 9, railY - 9, 18, 18);
            graphics.FillEllipse(node, centerX - 6, railY - 6, 12, 12);

            Rectangle labelBounds = new(centerX - 42, 31, 84, Math.Max(16, Height - 31));
            TextRenderer.DrawText(
                graphics,
                Labels[index],
                Font,
                labelBounds,
                nodeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }

    private Color NodeColor(int index)
    {
        if (_completed || index < _activeIndex) return UpdaterTheme.Blue;
        if (index > _activeIndex) return UpdaterTheme.Muted;
        return _failed ? UpdaterTheme.Red : _warning ? UpdaterTheme.Amber : UpdaterTheme.Teal;
    }

    private static int PhaseIndex(UpdateProgressStage stage) => stage switch
    {
        UpdateProgressStage.Preparing or UpdateProgressStage.Checking => 0,
        UpdateProgressStage.Downloading => 1,
        UpdateProgressStage.Verifying or UpdateProgressStage.Extracting => 2,
        UpdateProgressStage.WaitingForProcesses or UpdateProgressStage.Installing => 3,
        UpdateProgressStage.Restarting or UpdateProgressStage.Completed => 4,
        _ => 0
    };
}
