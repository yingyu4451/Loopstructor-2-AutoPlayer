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
                    progress?.Invoke($"Waiting for process {processId} to exit...");
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
            throw new TimeoutException("Timed out waiting for Manager or game processes to exit.");
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }
}
