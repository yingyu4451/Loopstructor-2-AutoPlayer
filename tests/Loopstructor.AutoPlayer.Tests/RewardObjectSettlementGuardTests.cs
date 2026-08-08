using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardObjectSettlementGuardTests
{
    [Fact]
    public void ArmedGuard_BlocksEveryAdditionalWriteUntilReset()
    {
        RewardObjectSettlementGuard guard = new();

        Assert.False(guard.TryArm(0, 9f));
        Assert.True(guard.TryArm(101, 10f));
        Assert.False(guard.TryArm(101, 10.1f));
        Assert.False(guard.TryArm(202, 10.1f));
        Assert.True(guard.IsArmed);
        Assert.Equal(101, guard.RewardObjectInstanceId);

        guard.Reset();

        Assert.True(guard.TryArm(202, 11f));
        Assert.Equal(202, guard.RewardObjectInstanceId);
    }

    [Fact]
    public void ArmedObjectDisappearing_SettlesCollection()
    {
        RewardObjectSettlementGuard guard = new();
        Assert.True(guard.TryArm(101, 10f));

        Assert.Equal(
            RewardObjectSettlementStatus.Settled,
            guard.Observe(new[] { 202 }, false, true, 10.5f, 20f));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RewardPhaseAdvancing_SettlesCollection(
        bool rewardPanelOrOptionsVisible,
        bool rewardBlockerVisible)
    {
        RewardObjectSettlementGuard guard = new();
        Assert.True(guard.TryArm(101, 10f));

        Assert.Equal(
            RewardObjectSettlementStatus.Settled,
            guard.Observe(
                new[] { 101 },
                rewardPanelOrOptionsVisible,
                rewardBlockerVisible,
                10.5f,
                20f));
    }

    [Fact]
    public void SameActiveObject_WaitsThenTimesOutWithoutDisarming()
    {
        RewardObjectSettlementGuard guard = new();
        Assert.True(guard.TryArm(101, 10f));

        Assert.Equal(
            RewardObjectSettlementStatus.Waiting,
            guard.Observe(new[] { 101, 202 }, false, true, 29.9f, 20f));
        Assert.Equal(
            RewardObjectSettlementStatus.TimedOut,
            guard.Observe(new[] { 101, 202 }, false, true, 30f, 20f));
        Assert.True(guard.IsArmed);
        Assert.Equal(101, guard.RewardObjectInstanceId);
    }

    [Fact]
    public void UnavailableObjectObservation_DoesNotFalselySettle()
    {
        RewardObjectSettlementGuard guard = new();
        Assert.True(guard.TryArm(101, 10f));

        Assert.Equal(
            RewardObjectSettlementStatus.Waiting,
            guard.Observe(null, false, true, 11f, 20f));
    }
}
