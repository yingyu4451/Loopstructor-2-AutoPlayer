using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed partial class UpdateForm : Window
{
    private const int MaximumLogEntries = 8;

    private readonly string _currentVersion;
    private readonly Func<
        IProgress<UpdateProgressSnapshot>,
        CancellationToken,
        Func<bool>,
        Task<UpdaterResult>> _operation;
    private readonly UpdateCommitGate _operationGate = new();
    private readonly object _progressSync = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<LogEntry> _recentLogs = new();
    private readonly List<UpdateProgressSnapshot> _pendingSnapshots = new();

    private UpdateProgressSnapshot? _lastSnapshot;
    private bool _started;
    private bool _running;
    private bool _allowClose;
    private bool _closeWhenFinished;
    private bool _currentCanCancel;
    private bool _cannotCloseNoticeShown;
    private bool _demoMode;
    private bool _resourcesReleased;
    private string _lastLoggedMessage = string.Empty;

    public UpdateForm(
        string currentVersion,
        Func<IProgress<UpdateProgressSnapshot>, CancellationToken, Func<bool>, Task<UpdaterResult>> operation)
    {
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));

        InitializeComponent();
        VersionLabel.Text = $"当前版本 v{_currentVersion}";
        DetailsBox.Document.PagePadding = new Thickness(0);
        DetailsBox.Document.Blocks.Clear();
        _progressTimer.Tick += (_, _) => DrainPendingSnapshot();
        StateChanged += UpdateFormOnStateChanged;
        UpdateWindowStateVisuals();

        ApplySnapshot(new UpdateProgressSnapshot
        {
            Stage = UpdateProgressStage.Preparing,
            OverallPercent = 0,
            Message = "正在初始化安全更新流程。",
            CanCancel = true
        });
    }

    public int ExitCode { get; private set; } = 1;

    public static UpdateForm CreateDemo(string currentVersion, string latestVersion)
    {
        string targetVersion = string.IsNullOrWhiteSpace(latestVersion) ? "0.1.7" : latestVersion.Trim();
        UpdateForm form = new(
            currentVersion,
            static (_, _, _) => Task.FromResult(new UpdaterResult
            {
                Success = true,
                Message = "演示更新已完成。"
            }))
        {
            _demoMode = true,
            _allowClose = true,
            ExitCode = 0
        };

        form.VersionLabel.Text = $"v{form._currentVersion}  →  v{targetVersion}";
        long total = 104L * 1024 * 1024;
        long downloaded = (long)(total * 0.45d);
        form.AppendLog("已连接 GitHub 发布资源。", UpdaterTheme.ConsoleText);
        form.ApplySnapshot(new UpdateProgressSnapshot
        {
            Stage = UpdateProgressStage.Downloading,
            OverallPercent = 45,
            Message = "正在下载 Loopstructor 2.AutoPlayer 更新包...",
            DownloadedBytes = downloaded,
            TotalBytes = total,
            BytesPerSecond = 8.4d * 1024 * 1024,
            CanCancel = true,
            IsFailure = false
        });
        form.FooterHint.Text = "演示状态，可直接关闭窗口。";
        form.ConfigureCloseButton();
        return form;
    }

    private async Task RunOperationAsync()
    {
        if (_started || _demoMode) return;
        _started = true;
        _running = true;
        _progressTimer.Start();
        ApplySnapshot(new UpdateProgressSnapshot
        {
            Stage = UpdateProgressStage.Preparing,
            OverallPercent = 0,
            Message = "正在初始化安全更新流程。",
            CanCancel = true
        });

        BufferedProgress progress = new(QueueProgress);
        try
        {
            UpdaterResult result = await _operation(
                progress,
                _operationGate.Token,
                _operationGate.TryBeginCommit);
            DrainPendingSnapshot();
            if (!result.Success)
            {
                if (_closeWhenFinished
                    && _operationGate.IsCancellationRequested
                    && !result.ManagerRestartFailed)
                {
                    CloseAfterCancellation();
                    return;
                }

                ShowFailure(string.IsNullOrWhiteSpace(result.Message) ? "更新未能完成。" : result.Message);
                return;
            }

            await ShowCompletionAsync(result);
        }
        catch (OperationCanceledException) when (_operationGate.IsCancellationRequested)
        {
            CloseAfterCancellation();
        }
        catch (Exception exception)
        {
            if (_closeWhenFinished && _operationGate.IsCancellationRequested)
            {
                CloseAfterCancellation();
                return;
            }

            ShowFailure("更新过程中发生错误：" + exception.Message);
        }
        finally
        {
            _progressTimer.Stop();
        }
    }

    private void QueueProgress(UpdateProgressSnapshot snapshot)
    {
        if (snapshot == null) return;
        lock (_progressSync)
        {
            bool coalescible = snapshot.Stage is UpdateProgressStage.Downloading or UpdateProgressStage.Extracting;
            if (coalescible
                && _pendingSnapshots.Count > 0
                && _pendingSnapshots[^1].Stage == snapshot.Stage
                && !_pendingSnapshots[^1].IsFailure
                && !snapshot.IsFailure)
            {
                _pendingSnapshots[^1] = snapshot;
            }
            else
            {
                _pendingSnapshots.Add(snapshot);
            }
        }
    }

    private void DrainPendingSnapshot()
    {
        UpdateProgressSnapshot[] snapshots;
        lock (_progressSync)
        {
            if (_pendingSnapshots.Count == 0) return;
            snapshots = _pendingSnapshots.ToArray();
            _pendingSnapshots.Clear();
        }

        foreach (UpdateProgressSnapshot snapshot in snapshots)
        {
            ApplySnapshot(snapshot);
        }
    }

    private void ApplySnapshot(UpdateProgressSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        int percent = Math.Clamp(snapshot.OverallPercent, 0, 100);
        bool completed = snapshot.Stage == UpdateProgressStage.Completed && !snapshot.IsFailure;
        StageRail.SetStage(snapshot.Stage, snapshot.IsFailure);
        OverallProgress.SetProgress(percent, snapshot.IsFailure, completed);
        PercentText.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
        StageTitle.Text = snapshot.IsFailure ? "更新失败" : StageTitleText(snapshot.Stage);
        StageDetail.Text = string.IsNullOrWhiteSpace(snapshot.Message)
            ? StageTitle.Text
            : Sanitize(snapshot.Message);
        DownloadedMetric.Text = DownloadMetric(snapshot.DownloadedBytes, snapshot.TotalBytes);
        SpeedMetric.Text = snapshot.BytesPerSecond > 0d && snapshot.Stage == UpdateProgressStage.Downloading
            ? FormatBytes((long)snapshot.BytesPerSecond) + "/s"
            : "--";

        Brush stateColor = BadgeColor(snapshot);
        SetStatusBadge(snapshot.IsFailure ? "更新失败" : BadgeText(snapshot.Stage), stateColor);
        StageBanner.Background = snapshot.IsFailure || snapshot.Stage == UpdateProgressStage.WaitingForProcesses
            ? UpdaterTheme.AlertSurface
            : UpdaterTheme.ActiveSurface;
        StageBanner.BorderBrush = stateColor;
        StageTitle.Foreground = stateColor;
        PercentText.Foreground = snapshot.IsFailure
            ? UpdaterTheme.Red
            : completed ? UpdaterTheme.SignalGreen : UpdaterTheme.Gold;
        SpeedMetric.Foreground = snapshot.Stage == UpdateProgressStage.Downloading
            ? UpdaterTheme.SignalGreen
            : UpdaterTheme.Muted;

        _currentCanCancel = _running
                            && _operationGate.CanCancel
                            && snapshot.CanCancel
                            && !snapshot.IsFailure
                            && !completed;
        if (_operationGate.IsCancellationRequested && _running)
        {
            ConfigureCancellationState();
        }
        else
        {
            ConfigureActionButton();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Message)
            && !string.Equals(_lastLoggedMessage, snapshot.Message, StringComparison.Ordinal))
        {
            _lastLoggedMessage = snapshot.Message;
            AppendLog(
                snapshot.Message,
                snapshot.IsFailure
                    ? UpdaterTheme.Red
                    : completed ? UpdaterTheme.SignalGreen : UpdaterTheme.ConsoleText);
        }
    }

    private void SetStatusBadge(string text, Brush color)
    {
        StatusBadgeText.Text = text;
        StatusIndicator.Fill = color;
        StatusBadgeBorder.BorderBrush = color;
        AutomationProperties.SetName(StatusBadgeBorder, "更新状态：" + text);
    }

    private void ConfigureActionButton()
    {
        if (!_running || _demoMode)
        {
            ConfigureCloseButton();
            return;
        }

        ActionButton.Content = _currentCanCancel ? "取消" : "请勿关闭";
        AutomationProperties.SetName(ActionButton, _currentCanCancel ? "取消更新" : "当前不能关闭");
        UpdaterTheme.SetCommandButtonColor(
            ActionButton,
            _currentCanCancel ? UpdaterTheme.Amber : UpdaterTheme.Disabled);
        ActionButton.IsEnabled = _currentCanCancel;
        ActionButton.Cursor = _currentCanCancel ? Cursors.Hand : Cursors.Arrow;
        FooterHint.Text = _currentCanCancel
            ? "当前阶段可安全取消。"
            : "正在执行关键步骤，完成前不能关闭。";
    }

    private void ConfigureCloseButton()
    {
        ActionButton.Content = "关闭";
        AutomationProperties.SetName(ActionButton, "关闭更新器");
        UpdaterTheme.SetCommandButtonColor(ActionButton, UpdaterTheme.Blue);
        ActionButton.IsEnabled = true;
        ActionButton.Cursor = Cursors.Hand;
    }

    private async Task ShowCompletionAsync(UpdaterResult result)
    {
        _running = false;
        ExitCode = 0;
        string message = string.IsNullOrWhiteSpace(result.Message) ? "更新已完成。" : result.Message;
        if (!string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            VersionLabel.Text = $"v{_currentVersion}  →  v{result.LatestVersion.Trim()}";
        }

        ApplySnapshot(new UpdateProgressSnapshot
        {
            Stage = UpdateProgressStage.Completed,
            OverallPercent = 100,
            Message = message,
            DownloadedBytes = _lastSnapshot?.DownloadedBytes ?? 0,
            TotalBytes = _lastSnapshot?.TotalBytes ?? 0,
            CanCancel = false,
            IsFailure = false
        });
        FooterHint.Text = "更新完成，本窗口将自动关闭。";
        _allowClose = true;
        if (result.ManagerRestartFailed)
        {
            StageRail.SetStage(UpdateProgressStage.Restarting, failed: false, warning: true);
            SetStatusBadge("需手动启动", UpdaterTheme.Amber);
            StageBanner.Background = UpdaterTheme.AlertSurface;
            StageBanner.BorderBrush = UpdaterTheme.Amber;
            StageTitle.Text = "更新完成，Manager 未启动";
            StageTitle.Foreground = UpdaterTheme.Amber;
            FooterHint.Text = "请关闭窗口后手动启动 Manager。";
            return;
        }

        if (_closeWhenFinished)
        {
            Close();
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        if (IsVisible) Close();
    }

    private void ShowFailure(string message)
    {
        _running = false;
        ExitCode = 1;
        UpdateProgressStage stage = _lastSnapshot?.Stage ?? UpdateProgressStage.Preparing;
        ApplySnapshot(new UpdateProgressSnapshot
        {
            Stage = stage,
            OverallPercent = _lastSnapshot?.OverallPercent ?? 0,
            Message = message,
            DownloadedBytes = _lastSnapshot?.DownloadedBytes ?? 0,
            TotalBytes = _lastSnapshot?.TotalBytes ?? 0,
            CanCancel = false,
            IsFailure = true
        });
        FooterHint.Text = "请查看详细状态，然后关闭窗口。";
        _allowClose = true;
    }

    private void RequestCancellationAndClose()
    {
        if (!_running || !_currentCanCancel || _operationGate.IsCancellationRequested) return;
        if (!_operationGate.TryCancel())
        {
            _currentCanCancel = false;
            ConfigureActionButton();
            return;
        }

        _closeWhenFinished = true;
        _currentCanCancel = false;
        ActionButton.IsEnabled = false;
        ConfigureCancellationState();
        AppendLog("已请求取消，正在等待安全清理。", UpdaterTheme.Amber);
    }

    private void ConfigureCancellationState()
    {
        _currentCanCancel = false;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "正在取消";
        UpdaterTheme.SetCommandButtonColor(ActionButton, UpdaterTheme.Disabled);
        ActionButton.Cursor = Cursors.Arrow;
        FooterHint.Text = "正在等待当前操作安全清理，请稍候。";
        StageTitle.Text = "正在取消更新";
        StageTitle.Foreground = UpdaterTheme.Amber;
        StageBanner.BorderBrush = UpdaterTheme.Amber;
        StageDetail.Text = "正在清理临时文件，完成后将关闭窗口。";
    }

    private void CloseAfterCancellation()
    {
        _running = false;
        ExitCode = 1;
        _allowClose = true;
        Close();
    }

    private void AppendLog(string message, Brush color)
    {
        string normalized = Sanitize(message);
        if (string.IsNullOrWhiteSpace(normalized)) return;
        _recentLogs.Add(new LogEntry(DateTime.Now, normalized, color));
        if (_recentLogs.Count > MaximumLogEntries)
        {
            _recentLogs.RemoveRange(0, _recentLogs.Count - MaximumLogEntries);
        }

        DetailsBox.Document.Blocks.Clear();
        Paragraph paragraph = new()
        {
            Margin = new Thickness(0),
            LineHeight = 19d
        };
        foreach (LogEntry entry in _recentLogs)
        {
            paragraph.Inlines.Add(new Run(entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ")
            {
                Foreground = UpdaterTheme.Muted
            });
            paragraph.Inlines.Add(new Run(entry.Message) { Foreground = entry.Color });
            paragraph.Inlines.Add(new LineBreak());
        }

        DetailsBox.Document.Blocks.Add(paragraph);
        DetailsBox.ScrollToEnd();
    }

    private void UpdateFormOnContentRendered(object sender, EventArgs eventArgs) => _ = RunOperationAsync();

    private void UpdateFormOnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose || !_running) return;
        eventArgs.Cancel = true;
        if (_currentCanCancel)
        {
            RequestCancellationAndClose();
            return;
        }

        FooterHint.Text = "当前阶段正在替换文件，完成前不能关闭。";
        if (_cannotCloseNoticeShown) return;
        _cannotCloseNoticeShown = true;
        AppendLog("当前处于关键更新阶段，已阻止关闭窗口。", UpdaterTheme.Amber);
    }

    private void UpdateFormOnClosed(object? sender, EventArgs eventArgs)
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        _progressTimer.Stop();
        _operationGate.Dispose();
    }

    private void ActionButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!_running)
        {
            _allowClose = true;
            Close();
            return;
        }

        RequestCancellationAndClose();
    }

    private void UpdateFormOnStateChanged(object? sender, EventArgs eventArgs) =>
        UpdateWindowStateVisuals();

    private void UpdateWindowStateVisuals()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;

        string actionName = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, actionName);
        MaximizeButton.ToolTip = actionName;
        WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(8);
        ResizeGrip.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
    }

    private void WindowMenuButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        Point menuPoint = WindowMenuButton.PointToScreen(new Point(0d, WindowMenuButton.ActualHeight));
        SystemCommands.ShowSystemMenu(this, menuPoint);
    }

    private void UpdateFormOnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        Point titleBarPoint = eventArgs.GetPosition(TitleBarHost);
        if (titleBarPoint.X < 0d
            || titleBarPoint.X > TitleBarHost.ActualWidth
            || titleBarPoint.Y < 0d
            || titleBarPoint.Y > TitleBarHost.ActualHeight)
        {
            return;
        }

        SystemCommands.ShowSystemMenu(this, PointToScreen(eventArgs.GetPosition(this)));
        eventArgs.Handled = true;
    }

    private void MinimizeButtonOnClick(object sender, RoutedEventArgs eventArgs) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeButtonOnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void CloseButtonOnClick(object sender, RoutedEventArgs eventArgs) =>
        SystemCommands.CloseWindow(this);

    private void ResizeGripOnDragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        if (WindowState != WindowState.Normal) return;
        Width = Math.Max(MinWidth, ActualWidth + eventArgs.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + eventArgs.VerticalChange);
    }

    private static string StageTitleText(UpdateProgressStage stage) => stage switch
    {
        UpdateProgressStage.Preparing => "正在准备更新",
        UpdateProgressStage.Checking => "正在检查发布版本",
        UpdateProgressStage.Downloading => "正在下载更新包",
        UpdateProgressStage.Verifying => "正在校验安装包",
        UpdateProgressStage.Extracting => "正在解压更新文件",
        UpdateProgressStage.WaitingForProcesses => "正在等待程序退出",
        UpdateProgressStage.Installing => "正在安装更新",
        UpdateProgressStage.Restarting => "正在启动 Manager",
        UpdateProgressStage.Completed => "更新完成",
        _ => "正在更新"
    };

    private static string BadgeText(UpdateProgressStage stage) => stage switch
    {
        UpdateProgressStage.Preparing => "准备中",
        UpdateProgressStage.Checking => "检查中",
        UpdateProgressStage.Downloading => "正在下载",
        UpdateProgressStage.Verifying => "正在校验",
        UpdateProgressStage.Extracting => "正在解压",
        UpdateProgressStage.WaitingForProcesses => "等待退出",
        UpdateProgressStage.Installing => "正在安装",
        UpdateProgressStage.Restarting => "正在重启",
        UpdateProgressStage.Completed => "已完成",
        _ => "更新中"
    };

    private static Brush BadgeColor(UpdateProgressSnapshot snapshot)
    {
        if (snapshot.IsFailure) return UpdaterTheme.Red;
        return snapshot.Stage switch
        {
            UpdateProgressStage.Completed => UpdaterTheme.SignalGreen,
            UpdateProgressStage.WaitingForProcesses => UpdaterTheme.Amber,
            _ => UpdaterTheme.SignalGreen
        };
    }

    private static string DownloadMetric(long downloaded, long total)
    {
        if (downloaded <= 0 && total <= 0) return "-";
        string downloadedText = FormatBytes(Math.Max(0, downloaded));
        string totalText = total > 0 ? FormatBytes(total) : "--";
        return downloadedText + " / " + totalText;
    }

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double scaled = Math.Max(0, value);
        int unit = 0;
        while (scaled >= 1024d && unit < units.Length - 1)
        {
            scaled /= 1024d;
            unit++;
        }

        string format = unit == 0 ? "0" : scaled >= 100d ? "0" : "0.0";
        return scaled.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string Sanitize(string value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed class BufferedProgress : IProgress<UpdateProgressSnapshot>
    {
        private readonly Action<UpdateProgressSnapshot> _report;

        public BufferedProgress(Action<UpdateProgressSnapshot> report)
        {
            _report = report;
        }

        public void Report(UpdateProgressSnapshot value) => _report(value);
    }

    private readonly record struct LogEntry(DateTime Timestamp, string Message, Brush Color);
}
