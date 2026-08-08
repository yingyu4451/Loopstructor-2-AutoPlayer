using System;

namespace Loopstructor.AutoPlayer.Core;

public enum RewardOptionObservationStatus
{
    WaitingForStableUi,
    RecordingStarted,
    Recording,
    Ready
}

public sealed class RewardOptionObservationDecision
{
    public RewardOptionObservationDecision(
        string fingerprint,
        float readyAt,
        bool fingerprintChanged,
        RewardOptionObservationStatus status)
    {
        Fingerprint = fingerprint;
        ReadyAt = readyAt;
        FingerprintChanged = fingerprintChanged;
        Status = status;
    }

    public string Fingerprint { get; }
    public float ReadyAt { get; }
    public bool FingerprintChanged { get; }
    public RewardOptionObservationStatus Status { get; }
}

public static class RewardOptionObservationGate
{
    public static RewardOptionObservationDecision Observe(
        string previousFingerprint,
        float previousReadyAt,
        string currentFingerprint,
        bool uiTransient,
        float now,
        float recordingDelaySeconds)
    {
        bool fingerprintChanged = !string.Equals(
            previousFingerprint ?? string.Empty,
            currentFingerprint ?? string.Empty,
            StringComparison.Ordinal);
        float readyAt = fingerprintChanged ? -1f : previousReadyAt;

        if (uiTransient)
        {
            return new RewardOptionObservationDecision(
                currentFingerprint ?? string.Empty,
                -1f,
                fingerprintChanged,
                RewardOptionObservationStatus.WaitingForStableUi);
        }

        if (readyAt < 0f)
        {
            readyAt = now + Math.Max(0f, recordingDelaySeconds);
            return new RewardOptionObservationDecision(
                currentFingerprint ?? string.Empty,
                readyAt,
                fingerprintChanged,
                RewardOptionObservationStatus.RecordingStarted);
        }

        return new RewardOptionObservationDecision(
            currentFingerprint ?? string.Empty,
            readyAt,
            fingerprintChanged,
            now < readyAt
                ? RewardOptionObservationStatus.Recording
                : RewardOptionObservationStatus.Ready);
    }
}
