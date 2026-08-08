using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardSelectionSettlementGuardTests
{
    [Fact]
    public void SameRewardIdentity_CannotBeArmedTwiceBeforeSettlement()
    {
        RewardSelectionSettlementGuard guard = new();

        Assert.True(guard.TryArm("phase-a", 101, 10f));
        Assert.False(guard.TryArm("phase-a", 101, 10.1f));
        Assert.False(guard.TryArm("phase-b", 202, 10.1f));
        Assert.True(guard.IsArmed);
        Assert.Equal("phase-a", guard.PhaseToken);
        Assert.Equal(101, guard.ItemInstanceId);
    }

    [Fact]
    public void SameVisibleIdentity_WaitsThenTimesOutWithoutChangingOwnership()
    {
        RewardSelectionSettlementGuard guard = new();
        Assert.True(guard.TryArm("phase-a", 101, 10f));

        Assert.Equal(
            RewardSelectionSettlementStatus.Waiting,
            guard.Observe(true, "phase-a", new[] { 101, 102 }, 29.9f, 20f));
        Assert.Equal(
            RewardSelectionSettlementStatus.TimedOut,
            guard.Observe(true, "phase-a", new[] { 101, 102 }, 30f, 20f));
        Assert.True(guard.IsArmed);
        Assert.Equal("phase-a", guard.PhaseToken);
        Assert.Equal(101, guard.ItemInstanceId);
    }

    [Theory]
    [InlineData(false, "phase-a", true)]
    [InlineData(true, "phase-b", true)]
    [InlineData(true, "phase-a", false)]
    public void PanelPhaseOrItemChange_SettlesTheIssuedSelection(
        bool panelOpen,
        string observedPhase,
        bool itemStillVisible)
    {
        RewardSelectionSettlementGuard guard = new();
        Assert.True(guard.TryArm("phase-a", 101, 10f));

        int[] visibleItems = itemStillVisible ? new[] { 101 } : new[] { 202 };
        Assert.Equal(
            RewardSelectionSettlementStatus.Settled,
            guard.Observe(panelOpen, observedPhase, visibleItems, 10.5f, 20f));
    }

    [Fact]
    public void NewIdentity_CanBeArmedOnlyAfterVerifiedSettlementIsReset()
    {
        RewardSelectionSettlementGuard guard = new();
        Assert.True(guard.TryArm("phase-a", 101, 10f));
        Assert.Equal(
            RewardSelectionSettlementStatus.Settled,
            guard.Observe(true, "phase-b", new[] { 202 }, 11f, 20f));

        guard.Reset();

        Assert.True(guard.TryArm("phase-b", 202, 11f));
        Assert.Equal("phase-b", guard.PhaseToken);
        Assert.Equal(202, guard.ItemInstanceId);
    }

    [Fact]
    public void MissingIdentity_NeverArmsAWriteLock()
    {
        RewardSelectionSettlementGuard guard = new();

        Assert.False(guard.TryArm(string.Empty, 101, 0f));
        Assert.False(guard.TryArm("phase-a", 0, 0f));
        Assert.False(guard.IsArmed);
    }
}
