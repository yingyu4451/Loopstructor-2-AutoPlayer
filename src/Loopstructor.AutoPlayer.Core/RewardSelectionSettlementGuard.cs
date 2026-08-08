using System;
using System.Collections.Generic;
using System.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum RewardSelectionSettlementStatus
{
    None,
    Waiting,
    Settled,
    TimedOut
}

/// <summary>
/// Prevents a reward option from being submitted more than once while the UI is settling.
/// </summary>
public sealed class RewardSelectionSettlementGuard
{
    public bool IsArmed { get; private set; }
    public string PhaseToken { get; private set; } = string.Empty;
    public int ItemInstanceId { get; private set; }
    public float StartedAt { get; private set; } = -1f;

    public bool TryArm(string phaseToken, int itemInstanceId, float now)
    {
        if (string.IsNullOrWhiteSpace(phaseToken) || itemInstanceId == 0)
        {
            return false;
        }

        if (IsArmed)
        {
            return false;
        }

        IsArmed = true;
        PhaseToken = phaseToken;
        ItemInstanceId = itemInstanceId;
        StartedAt = now;
        return true;
    }

    public RewardSelectionSettlementStatus Observe(
        bool panelOpen,
        string phaseToken,
        IEnumerable<int>? visibleItemInstanceIds,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return RewardSelectionSettlementStatus.None;
        }

        if (!panelOpen)
        {
            return RewardSelectionSettlementStatus.Settled;
        }

        if (!string.IsNullOrWhiteSpace(phaseToken) &&
            !string.Equals(PhaseToken, phaseToken, StringComparison.Ordinal))
        {
            return RewardSelectionSettlementStatus.Settled;
        }

        if (visibleItemInstanceIds != null && !visibleItemInstanceIds.Contains(ItemInstanceId))
        {
            return RewardSelectionSettlementStatus.Settled;
        }

        float timeout = Math.Max(0.1f, timeoutSeconds);
        return now - StartedAt >= timeout
            ? RewardSelectionSettlementStatus.TimedOut
            : RewardSelectionSettlementStatus.Waiting;
    }

    public void Reset()
    {
        IsArmed = false;
        PhaseToken = string.Empty;
        ItemInstanceId = 0;
        StartedAt = -1f;
    }
}
