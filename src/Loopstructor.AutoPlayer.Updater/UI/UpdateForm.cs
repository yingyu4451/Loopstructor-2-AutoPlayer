using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Updater.UI;

internal sealed class UpdateForm : Form
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
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 100 };
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
    private string _lastLoggedMessage = string.Empty;

    private Label _versionLabel = null!;
    private UpdateStatusBadge _statusBadge = null!;
    private UpdateStageRail _stageRail = null!;
    private Panel _stageBanner = null!;
    private Label _stageTitle = null!;
    private Label _stageDetail = null!;
    private Label _percent = null!;
    private FlatProgressBar _progressBar = null!;
    private UpdateMetricDisplay _downloaded = null!;
    private UpdateMetricDisplay _speed = null!;
    private RichTextBox _details = null!;
    private Label _footerHint = null!;
    private Button _cancelButton = null!;

    public UpdateForm(
        string currentVersion,
        Func<IProgress<UpdateProgressSnapshot>, CancellationToken, Func<bool>, Task<UpdaterResult>> operation)
    {
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));

        InitializeWindow();
        BuildInterface();

        _progressTimer.Tick += (_, _) => DrainPendingSnapshot();
        Shown += (_, _) => _ = RunOperationAsync();
        FormClosing += UpdateFormOnFormClosing;
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

        form._versionLabel.Text = $"v{form._currentVersion}  ->  v{targetVersion}";
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
        form._footerHint.Text = "演示状态，可直接关闭窗口。";
        form.ConfigureCloseButton();
        return form;
    }

    private void InitializeWindow()
    {
        Text = "Loopstructor 2.AutoPlayer Updater";
        BackColor = UpdaterTheme.Canvas;
        ForeColor = UpdaterTheme.Ink;
        Font = UpdaterTheme.Body(9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(620, 520);
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
    }

    private void BuildInterface()
    {
        SuspendLayout();
        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = UpdaterTheme.Canvas
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(BuildHeader(), 0, 0);
        shell.Controls.Add(BuildBody(), 0, 1);
        Controls.Add(shell);
        ResumeLayout(performLayout: true);
    }

    private Control BuildHeader()
    {
        Panel header = new()
        {
            Dock = DockStyle.Fill,
            BackColor = UpdaterTheme.Ink,
            Padding = new Padding(20, 10, 16, 10),
            Margin = Padding.Empty
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));

        Panel identity = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        Label title = new()
        {
            AutoSize = true,
            Text = "SKYSPINE  /  AUTOPLAYER UPDATER",
            ForeColor = Color.White,
            Font = UpdaterTheme.Display(14f, FontStyle.Bold),
            Location = Point.Empty
        };
        _versionLabel = new Label
        {
            AutoSize = true,
            Text = $"当前版本 v{_currentVersion}",
            ForeColor = Color.FromArgb(177, 191, 199),
            Font = UpdaterTheme.Data(8.5f, FontStyle.Bold),
            Location = new Point(2, 31)
        };
        identity.Controls.Add(title);
        identity.Controls.Add(_versionLabel);

        FlowLayoutPanel badgeHost = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 9, 0, 0),
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        _statusBadge = new UpdateStatusBadge { Margin = Padding.Empty };
        badgeHost.Controls.Add(_statusBadge);

        layout.Controls.Add(identity, 0, 0);
        layout.Controls.Add(badgeHost, 1, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Control BuildBody()
    {
        Panel host = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = UpdaterTheme.Canvas,
            Margin = Padding.Empty
        };
        UpdaterSectionPanel section = new() { Dock = DockStyle.Fill };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
            BackColor = UpdaterTheme.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _stageRail = new UpdateStageRail { Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(_stageRail, 0, 0);
        layout.Controls.Add(BuildStageBanner(), 0, 1);
        layout.Controls.Add(BuildProgressHeader(), 0, 2);

        _progressBar = new FlatProgressBar
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 2)
        };
        layout.Controls.Add(_progressBar, 0, 3);
        layout.Controls.Add(BuildMetrics(), 0, 4);

        Label detailHeading = new()
        {
            Dock = DockStyle.Fill,
            Text = "详细状态",
            ForeColor = UpdaterTheme.Ink,
            Font = UpdaterTheme.Body(8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        layout.Controls.Add(detailHeading, 0, 5);

        _details = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = UpdaterTheme.Console,
            ForeColor = UpdaterTheme.ConsoleText,
            Font = UpdaterTheme.Data(8.5f),
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            ShortcutsEnabled = true,
            Margin = Padding.Empty,
            AccessibleName = "更新详细状态"
        };
        layout.Controls.Add(_details, 0, 6);
        layout.Controls.Add(BuildFooter(), 0, 7);

        section.Controls.Add(layout);
        host.Controls.Add(section);
        return host;
    }

    private Control BuildStageBanner()
    {
        _stageBanner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UpdaterTheme.ActiveSurface,
            Padding = new Padding(12, 8, 12, 7),
            Margin = new Padding(0, 0, 0, 7)
        };
        _stageTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "正在准备更新",
            ForeColor = UpdaterTheme.TealDark,
            Font = UpdaterTheme.Body(11f, FontStyle.Bold),
            AutoEllipsis = true
        };
        _stageDetail = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在初始化安全更新流程。",
            ForeColor = UpdaterTheme.Muted,
            Font = UpdaterTheme.Body(8.5f),
            AutoEllipsis = true
        };
        _stageBanner.Controls.Add(_stageDetail);
        _stageBanner.Controls.Add(_stageTitle);
        return _stageBanner;
    }

    private Control BuildProgressHeader()
    {
        TableLayoutPanel header = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        Label caption = UpdaterTheme.Caption("总进度");
        caption.Dock = DockStyle.Fill;
        caption.TextAlign = ContentAlignment.MiddleLeft;
        _percent = new Label
        {
            Dock = DockStyle.Fill,
            Text = "0%",
            ForeColor = UpdaterTheme.Ink,
            Font = UpdaterTheme.Data(11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty
        };
        header.Controls.Add(caption, 0, 0);
        header.Controls.Add(_percent, 1, 0);
        return header;
    }

    private Control BuildMetrics()
    {
        TableLayoutPanel metrics = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 4),
            Margin = Padding.Empty
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _downloaded = new UpdateMetricDisplay("已下载 / 总大小");
        metrics.Controls.Add(_downloaded, 0, 0);
        metrics.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UpdaterTheme.Line,
            Margin = new Padding(0, 2, 0, 2)
        }, 1, 0);
        _speed = new UpdateMetricDisplay("速度")
        {
            Padding = new Padding(16, 0, 0, 0)
        };
        metrics.Controls.Add(_speed, 2, 0);
        return metrics;
    }

    private Control BuildFooter()
    {
        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 10, 0, 0),
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _footerHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "当前阶段可安全取消。",
            ForeColor = UpdaterTheme.Muted,
            Font = UpdaterTheme.Body(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        };
        _cancelButton = UpdaterTheme.CommandButton("取消", UpdaterTheme.Amber, 104);
        _cancelButton.Dock = DockStyle.Right;
        _cancelButton.Click += (_, _) => CancelButtonOnClick();
        footer.Controls.Add(_footerHint, 0, 0);
        footer.Controls.Add(_cancelButton, 1, 0);
        return footer;
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
        _stageRail.SetStage(snapshot.Stage, snapshot.IsFailure);
        _progressBar.SetProgress(percent, snapshot.IsFailure, completed);
        _percent.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
        _stageTitle.Text = snapshot.IsFailure ? "更新失败" : StageTitle(snapshot.Stage);
        _stageDetail.Text = string.IsNullOrWhiteSpace(snapshot.Message)
            ? _stageTitle.Text
            : Sanitize(snapshot.Message);
        _downloaded.SetValue(DownloadMetric(snapshot.DownloadedBytes, snapshot.TotalBytes));
        _speed.SetValue(snapshot.BytesPerSecond > 0d && snapshot.Stage == UpdateProgressStage.Downloading
            ? FormatBytes((long)snapshot.BytesPerSecond) + "/s"
            : "--");

        Color stateColor = BadgeColor(snapshot);
        _statusBadge.SetState(snapshot.IsFailure ? "更新失败" : BadgeText(snapshot.Stage), stateColor);
        _stageBanner.BackColor = snapshot.IsFailure || snapshot.Stage == UpdateProgressStage.WaitingForProcesses
            ? UpdaterTheme.AlertSurface
            : UpdaterTheme.ActiveSurface;
        _stageTitle.ForeColor = snapshot.IsFailure
            ? UpdaterTheme.Red
            : completed ? UpdaterTheme.Blue
            : snapshot.Stage == UpdateProgressStage.WaitingForProcesses
                ? UpdaterTheme.Amber
                : UpdaterTheme.TealDark;

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
                snapshot.IsFailure ? UpdaterTheme.Red : completed ? UpdaterTheme.Blue : UpdaterTheme.ConsoleText);
        }
    }

    private void ConfigureActionButton()
    {
        if (!_running || _demoMode)
        {
            ConfigureCloseButton();
            return;
        }

        _cancelButton.Text = _currentCanCancel ? "取消" : "请勿关闭";
        UpdaterTheme.SetCommandButtonColor(
            _cancelButton,
            _currentCanCancel ? UpdaterTheme.Amber : UpdaterTheme.Muted);
        _cancelButton.Enabled = _currentCanCancel;
        _cancelButton.Cursor = _currentCanCancel ? Cursors.Hand : Cursors.Default;
        _footerHint.Text = _currentCanCancel
            ? "当前阶段可安全取消。"
            : "正在执行关键步骤，完成前不能关闭。";
    }

    private void ConfigureCloseButton()
    {
        _cancelButton.Text = "关闭";
        UpdaterTheme.SetCommandButtonColor(_cancelButton, UpdaterTheme.Blue);
        _cancelButton.Enabled = true;
        _cancelButton.Cursor = Cursors.Hand;
    }

    private async Task ShowCompletionAsync(UpdaterResult result)
    {
        _running = false;
        ExitCode = 0;
        string message = string.IsNullOrWhiteSpace(result.Message) ? "更新已完成。" : result.Message;
        if (!string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            _versionLabel.Text = $"v{_currentVersion}  ->  v{result.LatestVersion.Trim()}";
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
        _footerHint.Text = "更新完成，本窗口将自动关闭。";
        _allowClose = true;
        if (result.ManagerRestartFailed)
        {
            _stageRail.SetStage(UpdateProgressStage.Restarting, failed: false, warning: true);
            _statusBadge.SetState("需手动启动", UpdaterTheme.Amber);
            _stageBanner.BackColor = UpdaterTheme.AlertSurface;
            _stageTitle.Text = "更新完成，Manager 未启动";
            _stageTitle.ForeColor = UpdaterTheme.Amber;
            _footerHint.Text = "请关闭窗口后手动启动 Manager。";
            return;
        }

        if (_closeWhenFinished)
        {
            Close();
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        if (!IsDisposed && Visible) Close();
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
        _footerHint.Text = "请查看详细状态，然后关闭窗口。";
        _allowClose = true;
    }

    private void CancelButtonOnClick()
    {
        if (!_running)
        {
            _allowClose = true;
            Close();
            return;
        }

        RequestCancellationAndClose();
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
        _cancelButton.Enabled = false;
        ConfigureCancellationState();
        AppendLog("已请求取消，正在等待安全清理。", UpdaterTheme.Amber);
    }

    private void ConfigureCancellationState()
    {
        _currentCanCancel = false;
        _cancelButton.Enabled = false;
        _cancelButton.Text = "正在取消";
        UpdaterTheme.SetCommandButtonColor(_cancelButton, UpdaterTheme.Muted);
        _cancelButton.Cursor = Cursors.Default;
        _footerHint.Text = "正在等待当前操作安全清理，请稍候。";
        _stageTitle.Text = "正在取消更新";
        _stageDetail.Text = "正在清理临时文件，完成后将关闭窗口。";
    }

    private void CloseAfterCancellation()
    {
        _running = false;
        ExitCode = 1;
        _allowClose = true;
        if (!IsDisposed) Close();
    }

    private void UpdateFormOnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || !_running) return;
        eventArgs.Cancel = true;
        if (_currentCanCancel)
        {
            RequestCancellationAndClose();
            return;
        }

        _footerHint.Text = "当前阶段正在替换文件，完成前不能关闭。";
        if (_cannotCloseNoticeShown) return;
        _cannotCloseNoticeShown = true;
        AppendLog("当前处于关键更新阶段，已阻止关闭窗口。", UpdaterTheme.Amber);
    }

    private void AppendLog(string message, Color color)
    {
        string normalized = Sanitize(message);
        if (string.IsNullOrWhiteSpace(normalized)) return;
        _recentLogs.Add(new LogEntry(DateTime.Now, normalized, color));
        if (_recentLogs.Count > MaximumLogEntries)
        {
            _recentLogs.RemoveRange(0, _recentLogs.Count - MaximumLogEntries);
        }

        _details.Clear();
        foreach (LogEntry entry in _recentLogs)
        {
            _details.SelectionColor = Color.FromArgb(145, 160, 168);
            _details.AppendText(entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ");
            _details.SelectionColor = entry.Color;
            _details.AppendText(entry.Message + Environment.NewLine);
        }

        _details.SelectionColor = _details.ForeColor;
        _details.SelectionStart = _details.TextLength;
        _details.ScrollToCaret();
    }

    private static string StageTitle(UpdateProgressStage stage) => stage switch
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

    private static Color BadgeColor(UpdateProgressSnapshot snapshot)
    {
        if (snapshot.IsFailure) return UpdaterTheme.Red;
        return snapshot.Stage switch
        {
            UpdateProgressStage.Completed => UpdaterTheme.Blue,
            UpdateProgressStage.WaitingForProcesses => UpdaterTheme.Amber,
            _ => UpdaterTheme.Teal
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressTimer.Stop();
            _progressTimer.Dispose();
            _operationGate.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class BufferedProgress : IProgress<UpdateProgressSnapshot>
    {
        private readonly Action<UpdateProgressSnapshot> _report;

        public BufferedProgress(Action<UpdateProgressSnapshot> report)
        {
            _report = report;
        }

        public void Report(UpdateProgressSnapshot value) => _report(value);
    }

    private readonly record struct LogEntry(DateTime Timestamp, string Message, Color Color);
}
