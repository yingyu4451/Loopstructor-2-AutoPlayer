using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class WaveFunctionOptionSettlementGuardTests
{
    [Fact]
    public void RepairIdentity_ArmsOnceAndBlocksEveryLaterOptionWrite()
    {
        WaveFunctionOptionSettlementGuard guard = new();
        AutomationAction action = RepairAction(panelId: 51, itemId: 101, index: 2);

        Assert.True(guard.TryArm(action, null, outcomeUnknown: true, now: 10f));
        Assert.False(guard.TryArm(action, null, outcomeUnknown: true, now: 10.1f));
        Assert.True(guard.IsArmed);
        Assert.Equal("RepairUI", guard.Panel);
        Assert.Equal(2, guard.Index);
        Assert.Equal(51, guard.PanelInstanceId);
        Assert.Equal(101, guard.ItemInstanceId);
        Assert.True(guard.OutcomeUnknown);
    }

    [Fact]
    public void MissingRepairOrOptionIdentity_DoesNotArm()
    {
        WaveFunctionOptionSettlementGuard guard = new();

        Assert.False(guard.TryArm(
            new AutomationAction(
                "chooseWaveFunctionOption",
                JObject.FromObject(new { panel = "ShopUI", index = 0, instanceId = 1 }),
                AutomationStage.ManagingEvent,
                "test"),
            null,
            outcomeUnknown: false,
            now: 0f));
        Assert.False(guard.TryArm(
            new AutomationAction(
                "chooseWaveFunctionOption",
                JObject.FromObject(new { panel = "RepairUI", index = 0 }),
                AutomationStage.ManagingEvent,
                "test"),
            null,
            outcomeUnknown: false,
            now: 0f));
        Assert.False(guard.IsArmed);
    }

    [Fact]
    public void PartialOrSameTargetSnapshot_CannotReleaseTheLock()
    {
        WaveFunctionOptionSettlementGuard guard = new();
        Assert.True(guard.TryArm(RepairAction(51, 101, 2), null, false, 10f));

        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Waiting,
            guard.ObserveOptions(QuerySnapshot(51, complete: false), 10.5f, 20f));
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Waiting,
            guard.ObserveOptions(QuerySnapshot(51, complete: true, 101, 202), 11f, 20f));
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void CompleteSnapshot_TargetDisappearanceOrPanelReplacementSettles()
    {
        WaveFunctionOptionSettlementGuard disappeared = new();
        Assert.True(disappeared.TryArm(RepairAction(51, 101, 2), null, false, 10f));
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Settled,
            disappeared.ObserveOptions(QuerySnapshot(51, complete: true, 202), 10.5f, 20f));

        WaveFunctionOptionSettlementGuard replaced = new();
        Assert.True(replaced.TryArm(RepairAction(51, 101, 2), null, false, 10f));
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Settled,
            replaced.ObserveOptions(QuerySnapshot(52, complete: true, 101), 10.5f, 20f));
    }

    [Fact]
    public void EventPanelUsesTheSameIdentityGuardAndItsOwnSnapshot()
    {
        WaveFunctionOptionSettlementGuard guard = new();
        AutomationAction action = new(
            "chooseWaveFunctionOption",
            JObject.FromObject(new
            {
                panel = "EventUI",
                index = 1,
                panelInstanceId = 71,
                instanceId = 301,
                optionIdentity = "text:event"
            }),
            AutomationStage.ManagingEvent,
            "test");
        Assert.True(guard.TryArm(action, null, false, 10f));

        JObject snapshot = JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    eventPanel = new
                    {
                        panelOpen = true,
                        panelInstanceId = 71,
                        snapshotComplete = true,
                        options = new[] { new { instanceId = 302 } }
                    }
                }
            }
        });
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Settled,
            guard.ObserveOptions(snapshot, 10.5f, 20f));
    }

    [Fact]
    public void AuthoritativeWaveState_OnlySettlesWhenRepairPanelIsAbsent()
    {
        WaveFunctionOptionSettlementGuard guard = new();
        Assert.True(guard.TryArm(RepairAction(51, 101, 2), null, false, 10f));

        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Waiting,
            guard.ObservePanelVisibility(false, false, 10.5f, 20f));
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Waiting,
            guard.ObservePanelVisibility(true, true, 11f, 20f));
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Settled,
            guard.ObservePanelVisibility(true, false, 11.5f, 20f));
    }

    [Fact]
    public void TimeoutKeepsTheLockAndLaterReadOnlyEvidenceCanStillSettle()
    {
        WaveFunctionOptionSettlementGuard guard = new();
        Assert.True(guard.TryArm(RepairAction(51, 101, 2), null, true, 10f));

        Assert.Equal(
            WaveFunctionOptionSettlementStatus.TimedOut,
            guard.ObserveOptions(QuerySnapshot(51, complete: true, 101), 30f, 20f));
        Assert.True(guard.IsArmed);
        Assert.Equal(
            WaveFunctionOptionSettlementStatus.Settled,
            guard.ObserveOptions(QuerySnapshot(51, complete: true, 202), 31f, 20f));

        guard.Reset();
        Assert.False(guard.IsArmed);
        Assert.Equal(WaveFunctionOptionSettlementStatus.None, guard.Status);
    }

    private static AutomationAction RepairAction(int panelId, int itemId, int index) =>
        new(
            "chooseWaveFunctionOption",
            JObject.FromObject(new
            {
                panel = "RepairUI",
                index,
                panelInstanceId = panelId,
                instanceId = itemId,
                optionIdentity = "name:repair|behaviours:WaveFunctionBehaviour"
            }),
            AutomationStage.ManagingEvent,
            "test");

    private static JObject QuerySnapshot(int panelId, bool complete, params int[] itemIds) =>
        JObject.FromObject(new
        {
            success = true,
            data = new
            {
                state = new
                {
                    repairPanel = new
                    {
                        panelOpen = true,
                        panelInstanceId = panelId,
                        snapshotComplete = complete,
                        options = itemIds.Select(instanceId => new { instanceId }).ToArray()
                    }
                }
            }
        });
}
