using System.Diagnostics;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class ProcessWaiter
{
    public async Task WaitForExitAsync(
        IEnumerable<int> processIds,
        TimeSpan timeout,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int[] ids = processIds
            .Where(processId => processId > 0 && processId != Environment.ProcessId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return;

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        List<Task> waits = new();
        List<Process> processes = new();
        try
        {
            foreach (int processId in ids)
            {
                try
                {
                    Process process = Process.GetProcessById(processId);
                    if (process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }

                    processes.Add(process);
                    progress?.Invoke($"正在等待进程 {processId} 退出...");
                    waits.Add(process.WaitForExitAsync(timeoutSource.Token));
                }
                catch (ArgumentException)
                {
                    // Process already exited.
                }
            }

            await Task.WhenAll(waits);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 Manager 或游戏进程退出时超时。");
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }
}
