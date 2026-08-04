using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RuntimeFeatureFlagTests
{
    [Fact]
    public void Set_ReportsOnlyActualStateChanges()
    {
        RuntimeFeatureFlag flag = new();

        Assert.False(flag.Value);
        Assert.True(flag.Set(true));
        Assert.True(flag.Value);
        Assert.False(flag.Set(true));
        Assert.True(flag.Set(false));
        Assert.False(flag.Value);
    }

    [Fact]
    public async Task Value_PublishesChangesAcrossThreads()
    {
        RuntimeFeatureFlag flag = new();
        Task<bool> observed = Task.Run(() =>
            SpinWait.SpinUntil(() => flag.Value, TimeSpan.FromSeconds(2)));

        flag.Set(true);

        Assert.True(await observed);
    }

    [Fact]
    public void ConcurrentWrites_RemainUsableAndFinalWriteWins()
    {
        RuntimeFeatureFlag flag = new();

        Parallel.For(0, 10_000, index => flag.Set((index & 1) == 0));

        flag.Set(false);
        Assert.False(flag.Value);
        flag.Set(true);
        Assert.True(flag.Value);
    }
}
