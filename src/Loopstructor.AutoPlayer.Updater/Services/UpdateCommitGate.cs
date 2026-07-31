namespace Loopstructor.AutoPlayer.Updater.Services;

internal sealed class UpdateCommitGate : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _state;

    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => Volatile.Read(ref _state) == 1;

    public bool CanCancel => Volatile.Read(ref _state) == 0;

    public bool TryCancel()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return false;
        }

        _cancellation.Cancel();
        return true;
    }

    public bool TryBeginCommit()
    {
        int previous = Interlocked.CompareExchange(ref _state, 2, 0);
        return previous is 0 or 2;
    }

    public void Dispose() => _cancellation.Dispose();
}
