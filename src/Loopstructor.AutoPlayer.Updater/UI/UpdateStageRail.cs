using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed class UpdateStageRail : FrameworkElement
{
    private static readonly string[] Labels = { "准备", "下载", "校验", "安装", "重启" };

    private int _activeIndex;
    private bool _failed;
    private bool _warning;
    private bool _completed;

    public UpdateStageRail()
    {
        MinWidth = 360;
        MinHeight = 64;
        AutomationProperties.SetName(this, "更新阶段");
    }

    public void SetStage(UpdateProgressStage stage, bool failed, bool warning = false)
    {
        _activeIndex = PhaseIndex(stage);
        _completed = stage == UpdateProgressStage.Completed && !failed && !warning;
        _failed = failed;
        _warning = warning;
        string description = failed
            ? $"{Labels[_activeIndex]}阶段失败"
            : warning
                ? $"{Labels[_activeIndex]}阶段需要处理"
                : _completed
                    ? "全部阶段已完成"
                    : $"当前处于{Labels[_activeIndex]}阶段";
        AutomationProperties.SetHelpText(this, description);
        ToolTip = description;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = Math.Max(1d, ActualWidth);
        const double sidePadding = 43d;
        const double railY = 23d;
        double usableWidth = Math.Max(1d, width - (sidePadding * 2d));
        double step = usableWidth / (Labels.Length - 1d);

        Pen trackShadow = new(UpdaterTheme.Canvas, 10d) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        Pen trackBase = new(UpdaterTheme.Line, 6d) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        drawingContext.DrawLine(trackShadow, new Point(sidePadding, railY), new Point(width - sidePadding, railY));
        drawingContext.DrawLine(trackBase, new Point(sidePadding, railY), new Point(width - sidePadding, railY));

        for (int index = 0; index < Labels.Length - 1; index++)
        {
            if (!_completed && index >= _activeIndex) continue;
            double startX = sidePadding + (index * step);
            double endX = sidePadding + ((index + 1) * step);
            Pen completedSegment = new(UpdaterTheme.Gold, 4d)
            {
                StartLineCap = PenLineCap.Square,
                EndLineCap = PenLineCap.Square
            };
            drawingContext.DrawLine(completedSegment, new Point(startX, railY), new Point(endX, railY));
        }

        for (double x = sidePadding + 13d; x < width - sidePadding; x += 18d)
        {
            drawingContext.DrawLine(
                new Pen(UpdaterTheme.Copper, 1d),
                new Point(x, railY - 4d),
                new Point(x, railY + 4d));
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int index = 0; index < Labels.Length; index++)
        {
            double centerX = sidePadding + (index * step);
            Brush nodeBrush = NodeColor(index);
            DrawGear(drawingContext, new Point(centerX, railY), nodeBrush, index == _activeIndex || _completed);

            FormattedText label = new(
                Labels[index],
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12d,
                nodeBrush,
                pixelsPerDip);
            drawingContext.DrawText(label, new Point(centerX - (label.Width / 2d), 44d));
        }
    }

    private static void DrawGear(DrawingContext drawingContext, Point center, Brush stateBrush, bool active)
    {
        Pen spokePen = new(stateBrush, active ? 2.4d : 1.7d);
        for (int tooth = 0; tooth < 8; tooth++)
        {
            double angle = tooth * Math.PI / 4d;
            Point inner = new(center.X + (Math.Cos(angle) * 8d), center.Y + (Math.Sin(angle) * 8d));
            Point outer = new(center.X + (Math.Cos(angle) * 12d), center.Y + (Math.Sin(angle) * 12d));
            drawingContext.DrawLine(spokePen, inner, outer);
        }

        drawingContext.DrawEllipse(UpdaterTheme.SurfaceRaised, new Pen(UpdaterTheme.Canvas, 3d), center, 10d, 10d);
        drawingContext.DrawEllipse(active ? stateBrush : UpdaterTheme.Canvas, new Pen(stateBrush, 2d), center, 6d, 6d);
        drawingContext.DrawEllipse(UpdaterTheme.Canvas, null, center, 2.2d, 2.2d);
    }

    private Brush NodeColor(int index)
    {
        if (_completed) return UpdaterTheme.SignalGreen;
        if (index < _activeIndex) return UpdaterTheme.Gold;
        if (index > _activeIndex) return UpdaterTheme.Muted;
        return _failed ? UpdaterTheme.Red : _warning ? UpdaterTheme.Amber : UpdaterTheme.SignalGreen;
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
