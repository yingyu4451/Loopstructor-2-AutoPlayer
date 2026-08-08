using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RewardOptionObservationGateTests
{
    [Fact]
    public void TransientUi_DoesNotStartRecordingDelayOrResetOnEveryPoll()
    {
        RewardOptionObservationDecision first = RewardOptionObservationGate.Observe(
            string.Empty,
            -1f,
            "option-set-a",
            true,
            10f,
            1.5f);
        RewardOptionObservationDecision second = RewardOptionObservationGate.Observe(
            first.Fingerprint,
            first.ReadyAt,
            "option-set-a",
            true,
            11f,
            1.5f);

        Assert.Equal(RewardOptionObservationStatus.WaitingForStableUi, first.Status);
        Assert.True(first.FingerprintChanged);
        Assert.Equal(-1f, first.ReadyAt);
        Assert.Equal(RewardOptionObservationStatus.WaitingForStableUi, second.Status);
        Assert.False(second.FingerprintChanged);
        Assert.Equal(-1f, second.ReadyAt);
    }

    [Fact]
    public void FirstStableObservation_StartsOneFullRecordingDelay()
    {
        RewardOptionObservationDecision stable = RewardOptionObservationGate.Observe(
            "option-set-a",
            -1f,
            "option-set-a",
            false,
            20f,
            1.5f);
        RewardOptionObservationDecision recording = RewardOptionObservationGate.Observe(
            stable.Fingerprint,
            stable.ReadyAt,
            "option-set-a",
            false,
            21f,
            1.5f);
        RewardOptionObservationDecision ready = RewardOptionObservationGate.Observe(
            recording.Fingerprint,
            recording.ReadyAt,
            "option-set-a",
            false,
            21.5f,
            1.5f);

        Assert.Equal(RewardOptionObservationStatus.RecordingStarted, stable.Status);
        Assert.Equal(21.5f, stable.ReadyAt);
        Assert.Equal(RewardOptionObservationStatus.Recording, recording.Status);
        Assert.Equal(21.5f, recording.ReadyAt);
        Assert.Equal(RewardOptionObservationStatus.Ready, ready.Status);
    }

    [Fact]
    public void ReturningToTransientUi_RestartsDelayOnlyAfterUiStabilizesAgain()
    {
        RewardOptionObservationDecision transient = RewardOptionObservationGate.Observe(
            "option-set-a",
            21.5f,
            "option-set-a",
            true,
            21f,
            1.5f);
        RewardOptionObservationDecision stableAgain = RewardOptionObservationGate.Observe(
            transient.Fingerprint,
            transient.ReadyAt,
            "option-set-a",
            false,
            22f,
            1.5f);

        Assert.Equal(RewardOptionObservationStatus.WaitingForStableUi, transient.Status);
        Assert.Equal(-1f, transient.ReadyAt);
        Assert.Equal(RewardOptionObservationStatus.RecordingStarted, stableAgain.Status);
        Assert.Equal(23.5f, stableAgain.ReadyAt);
    }

    [Fact]
    public void NewOptionIdentity_AlwaysRequiresANewRecordingDelay()
    {
        RewardOptionObservationDecision changed = RewardOptionObservationGate.Observe(
            "option-set-a",
            15f,
            "option-set-b",
            false,
            30f,
            1.5f);

        Assert.True(changed.FingerprintChanged);
        Assert.Equal(RewardOptionObservationStatus.RecordingStarted, changed.Status);
        Assert.Equal(31.5f, changed.ReadyAt);
    }
}
