using System;
using System.Collections.Generic;
using System.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum RewardObjectSettlementStatus
{
    None,
    Waiting,
    Settled,
    TimedOut
}

/// <summary>
/// Prevents the same active reward object from being collected more than once
/// while the reward UI is advancing to its next phase.
/// </summary>
public sealed class RewardObjectSettlementGuard
{
    public bool IsArmed { get; private set; }
    public int RewardObjectInstanceId { get; private set; }
    public float StartedAt { get; private set; } = -1f;

    public bool TryArm(int rewardObjectInstanceId, float now)
    {
        if (rewardObjectInstanceId == 0 || IsArmed)
        {
            return false;
        }

        IsArmed = true;
        RewardObjectInstanceId = rewardObjectInstanceId;
        StartedAt = now;
        return true;
    }

    /// <summary>
    /// Observes the post-collection state. A known empty object collection is a
    /// valid observation; a null collection means that object visibility could
    /// not be observed and therefore cannot prove settlement by itself.
    /// </summary>
    public RewardObjectSettlementStatus Observe(
        IEnumerable<int>? activeRewardObjectInstanceIds,
        bool rewardPanelOrOptionsVisible,
        bool rewardBlockerVisible,
        float now,
        float timeoutSeconds)
    {
        if (!IsArmed)
        {
            return RewardObjectSettlementStatus.None;
        }

        if (rewardPanelOrOptionsVisible || !rewardBlockerVisible)
        {
            return RewardObjectSettlementStatus.Settled;
        }

        if (activeRewardObjectInstanceIds != null &&
            !activeRewardObjectInstanceIds.Contains(RewardObjectInstanceId))
        {
            return RewardObjectSettlementStatus.Settled;
        }

        float timeout = Math.Max(0.1f, timeoutSeconds);
        return now - StartedAt >= timeout
            ? RewardObjectSettlementStatus.TimedOut
            : RewardObjectSettlementStatus.Waiting;
    }

    public void Reset()
    {
        IsArmed = false;
        RewardObjectInstanceId = 0;
        StartedAt = -1f;
    }
}
