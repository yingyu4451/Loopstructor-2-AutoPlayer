using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GameUpdateShutdownCoordinatorTests
{
    [Fact]
    public async Task RequestCloseAndWait_RequestsNormalCloseAndWaitsForEveryGame()
    {
        FakeGameProcess first = new(42, closeAccepted: true, exitAfterClose: true);
        FakeGameProcess second = new(84, closeAccepted: true, exitAfterClose: true);
        GameUpdateShutdownCoordinator coordinator = new();

        GameUpdateShutdownResult result = await coordinator.RequestCloseAndWaitAsync(
            new IUpdateGameProcess[] { second, first },
            TimeSpan.FromSeconds(1));

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.RemainingProcessIds);
        Assert.Equal(1, first.CloseRequests);
        Assert.Equal(1, second.CloseRequests);
        Assert.Equal(1, first.WaitRequests);
        Assert.Equal(1, second.WaitRequests);
    }

    [Fact]
    public async Task RequestCloseAndWait_DoesNotStartUpdateWhenGameRejectsNormalClose()
    {
        FakeGameProcess process = new(42, closeAccepted: false, exitAfterClose: false);
        GameUpdateShutdownCoordinator coordinator = new();

        GameUpdateShutdownResult result = await coordinator.RequestCloseAndWaitAsync(
            new IUpdateGameProcess[] { process },
            TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.Equal(new[] { 42 }, result.RemainingProcessIds);
        Assert.Contains("手动退出", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, process.CloseRequests);
        Assert.Equal(0, process.WaitRequests);
    }

    [Fact]
    public async Task RequestCloseAndWait_TimesOutWithoutForcingTheGameProcess()
    {
        FakeGameProcess process = new(42, closeAccepted: true, exitAfterClose: false);
        GameUpdateShutdownCoordinator coordinator = new();

        GameUpdateShutdownResult result = await coordinator.RequestCloseAndWaitAsync(
            new IUpdateGameProcess[] { process },
            TimeSpan.FromMilliseconds(50));

        Assert.False(result.Success);
        Assert.Equal(new[] { 42 }, result.RemainingProcessIds);
        Assert.Contains("超时", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, process.CloseRequests);
        Assert.Equal(1, process.WaitRequests);
        Assert.False(process.ForcedExitAttempted);
    }

    [Fact]
    public async Task RequestCloseAndWait_WaitsForAcceptedProcessesWhenAnotherRejectsClose()
    {
        FakeGameProcess accepted = new(42, closeAccepted: true, exitAfterClose: true);
        FakeGameProcess rejected = new(84, closeAccepted: false, exitAfterClose: false);
        GameUpdateShutdownCoordinator coordinator = new();

        GameUpdateShutdownResult result = await coordinator.RequestCloseAndWaitAsync(
            new IUpdateGameProcess[] { accepted, rejected },
            TimeSpan.FromMilliseconds(50));

        Assert.False(result.Success);
        Assert.Equal(new[] { 84 }, result.RemainingProcessIds);
        Assert.Contains("仍在运行", result.Message, StringComparison.Ordinal);
        Assert.True(accepted.HasExited);
        Assert.Equal(1, accepted.WaitRequests);
    }

    private sealed class FakeGameProcess : IUpdateGameProcess
    {
        private readonly bool _closeAccepted;
        private readonly bool _exitAfterClose;

        internal FakeGameProcess(int id, bool closeAccepted, bool exitAfterClose)
        {
            Id = id;
            _closeAccepted = closeAccepted;
            _exitAfterClose = exitAfterClose;
        }

        public int Id { get; }
        public bool HasExited { get; private set; }
        public int CloseRequests { get; private set; }
        public int WaitRequests { get; private set; }
        public bool ForcedExitAttempted { get; private set; }

        public bool RequestClose()
        {
            CloseRequests++;
            if (_closeAccepted && _exitAfterClose) HasExited = true;
            return _closeAccepted;
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitRequests++;
            if (HasExited) return;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
