using System.ComponentModel;
using System.Diagnostics;

namespace Loopstructor.AutoPlayer.Manager.Services;

internal interface IUpdateGameProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    bool RequestClose();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed record GameUpdateShutdownResult(
    bool Success,
    IReadOnlyList<int> RemainingProcessIds,
    string Message);

internal sealed class GameUpdateShutdownCoordinator
{
    internal static IReadOnlyList<IUpdateGameProcess> FindRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return Array.Empty<IUpdateGameProcess>();
        }

        string expectedPath;
        try
        {
            expectedPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<IUpdateGameProcess>();
        }

        string processName = Path.GetFileNameWithoutExtension(expectedPath);
        List<IUpdateGameProcess> matches = new();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                string? actualPath = process.MainModule?.FileName;
                if (!process.HasExited && SamePath(actualPath, expectedPath))
                {
                    matches.Add(new SystemUpdateGameProcess(process));
                    continue;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Processes outside the current desktop/user boundary are not controllable.
            }

            process.Dispose();
        }

        matches.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return matches;
    }

    internal async Task<GameUpdateShutdownResult> RequestCloseAndWaitAsync(
        IReadOnlyCollection<IUpdateGameProcess> processes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        IUpdateGameProcess[] targets = processes
            .Where(process => !SafeHasExited(process))
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .OrderBy(process => process.Id)
            .ToArray();
        if (targets.Length == 0)
        {
            return new GameUpdateShutdownResult(true, Array.Empty<int>(), "游戏已经关闭，可以开始更新。");
        }

        HashSet<int> waitForExit = new();
        List<int> rejected = new();
        foreach (IUpdateGameProcess process in targets)
        {
            try
            {
                if (SafeHasExited(process))
                {
                    continue;
                }

                if (process.RequestClose() || SafeHasExited(process))
                {
                    waitForExit.Add(process.Id);
                }
                else
                {
                    rejected.Add(process.Id);
                }
            }
            catch (Exception)
            {
                if (!SafeHasExited(process)) rejected.Add(process.Id);
            }
        }

        if (rejected.Count > 0 && waitForExit.Count == 0)
        {
            return new GameUpdateShutdownResult(
                false,
                rejected,
                $"Skyspine 没有接受正常关闭请求（PID {string.Join("、", rejected)}）。请先在游戏内保存并手动退出，再重新安装更新。");
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(targets
                .Where(process => waitForExit.Contains(process.Id))
                .Select(process => process.WaitForExitAsync(timeoutSource.Token)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            int[] remaining = targets.Where(process => !SafeHasExited(process)).Select(process => process.Id).ToArray();
            if (remaining.Length == 0)
            {
                return new GameUpdateShutdownResult(true, Array.Empty<int>(), "Skyspine 已正常关闭，可以开始更新。");
            }

            return new GameUpdateShutdownResult(
                false,
                remaining,
                $"等待 Skyspine 正常退出超时（PID {string.Join("、", remaining)}）。更新尚未启动，请先手动关闭游戏后重试。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        int[] stillRunning = targets.Where(process => !SafeHasExited(process)).Select(process => process.Id).ToArray();
        return stillRunning.Length == 0
            ? new GameUpdateShutdownResult(true, Array.Empty<int>(), "Skyspine 已正常关闭，可以开始更新。")
            : new GameUpdateShutdownResult(
                false,
                stillRunning,
                $"Skyspine 仍在运行（PID {string.Join("、", stillRunning)}），更新尚未启动。");
    }

    private static bool SafeHasExited(IUpdateGameProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed class SystemUpdateGameProcess : IUpdateGameProcess
    {
        private readonly Process _process;

        internal SystemUpdateGameProcess(Process process)
        {
            _process = process;
        }

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public bool RequestClose() => HasExited || _process.CloseMainWindow();

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            HasExited ? Task.CompletedTask : _process.WaitForExitAsync(cancellationToken);

        public void Dispose() => _process.Dispose();
    }
}
