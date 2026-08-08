using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MergeMutationSettlementGuardTests
{
    [Fact]
    public void SelectVehicle_CapturesFullStableIdentityAndBlocksEveryAdditionalWrite()
    {
        MergeMutationSettlementGuard guard = new();
        AutomationAction action = SelectVehicleAction();

        Assert.True(guard.TryArm(action, SelectionState(selected: false), outcomeUnknown: true, now: 10f));
        Assert.False(guard.TryArm(action, SelectionState(selected: false), outcomeUnknown: true, now: 10.1f));
        Assert.False(guard.TryArm(Action("submitMergeSelection"), SelectionState(false), false, 10.1f));

        Assert.Equal("selectMergeVehicle", guard.Command);
        Assert.Equal(700, guard.PanelIdentity);
        Assert.Equal("roster-v1", guard.RosterIdentity);
        Assert.Equal("item:1004,vehicle:5004", guard.VehicleIdentity);
        Assert.Contains("material:Shell_L1", guard.GroupIdentity);
        Assert.Contains("result:Shell_L2", guard.GroupIdentity);
        Assert.Contains("items:1004,1009", guard.GroupIdentity);
        Assert.True(guard.OutcomeUnknown);
        Assert.True(guard.IsReplay(action, SelectionState(false)));
        Assert.False(guard.IsReplay(Action("submitMergeSelection"), SelectionState(false)));
    }

    [Fact]
    public void UnsafeRuntimeResult_RecordsUnknownOutcomeWithoutCallerGuessing()
    {
        MergeMutationSettlementGuard guard = new();
        JObject result = SelectionState(false);
        ((JObject)result.SelectToken("data.state")!)["invocationStarted"] = true;
        ((JObject)result.SelectToken("data.state")!)["selectionWriteVerified"] = false;

        Assert.True(guard.TryArm(SelectVehicleAction(), result, outcomeUnknown: false, now: 1f));
        Assert.True(guard.OutcomeUnknown);
    }

    [Fact]
    public void TargetBecomingSelected_SettlesWithoutReplayingTheClick()
    {
        MergeMutationSettlementGuard guard = ArmedSelect(10f);

        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            guard.Observe(SelectionState(false), 10.5f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            guard.Observe(SelectionState(true), 11f, 20f));
        Assert.True(guard.IsArmed);
        Assert.False(guard.TryArm(SelectVehicleAction(), SelectionState(true), false, 11.1f));
    }

    [Theory]
    [InlineData(false, 700, "roster-v1")]
    [InlineData(true, 701, "roster-v1")]
    [InlineData(true, 700, "roster-v2")]
    public void ClosedOrReplacedPanel_SettlesAnyRetainedWrite(
        bool mergeOpen,
        int panelInstanceId,
        string rosterFingerprint)
    {
        MergeMutationSettlementGuard guard = ArmedSelect(10f);
        JObject result = SelectionState(false, mergeOpen, panelInstanceId, rosterFingerprint);

        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            guard.Observe(result, 10.5f, 20f));
    }

    [Fact]
    public void MissingOrPartialReadOnlyState_NeverFalselySettles()
    {
        MergeMutationSettlementGuard guard = ArmedSelect(10f);

        Assert.Equal(MergeMutationSettlementStatus.Waiting, guard.Observe(null, 11f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            guard.Observe(new JObject { ["success"] = true }, 12f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            guard.Observe(Result(new { phase = "selection" }), 13f, 20f));
    }

    [Fact]
    public void TimedOutSelection_RemainsLockedButCanStillSettleAfterLaterProgress()
    {
        MergeMutationSettlementGuard guard = ArmedSelect(10f);

        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            guard.Observe(SelectionState(false), 29.99f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.TimedOut,
            guard.Observe(SelectionState(false), 30f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            guard.Observe(SelectionState(true), 30.1f, 20f));
        Assert.True(guard.IsArmed);
        Assert.False(guard.TryArm(SelectVehicleAction(), SelectionState(false), true, 31f));
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("candidate")]
    [InlineData("settlement")]
    public void SubmitSelection_SettlesOnlyAfterPanelAdvancesFromSelection(string phase)
    {
        MergeMutationSettlementGuard guard = new();
        Assert.True(guard.TryArm(
            Action("submitMergeSelection"),
            SelectionState(false, selectedVehicleCount: 2),
            outcomeUnknown: true,
            now: 10f));

        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            guard.Observe(PanelState(phase, selectedVehicleCount: 2), 11f, 20f));
    }

    [Fact]
    public void SubmitSelection_SamePhaseAndSelectionWaitsButSelectionResetSettles()
    {
        MergeMutationSettlementGuard guard = new();
        Assert.True(guard.TryArm(
            Action("submitMergeSelection"),
            SelectionState(false, selectedVehicleCount: 2),
            outcomeUnknown: true,
            now: 10f));

        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            guard.Observe(PanelState("selection", selectedVehicleCount: 2), 11f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            guard.Observe(PanelState("selection", selectedVehicleCount: 0), 12f, 20f));
    }

    [Theory]
    [InlineData("chooseMergeFetter", "candidate", false)]
    [InlineData("chooseMergeFetter", "transition", true)]
    [InlineData("chooseMergeFetter", "settlement", true)]
    [InlineData("confirmMergeSettlement", "settlement", false)]
    [InlineData("confirmMergeSettlement", "selection", true)]
    [InlineData("closeMergePanel", "selection", false)]
    public void CommandSpecificPanelPhaseControlsSettlement(
        string command,
        string phase,
        bool expectedSettled)
    {
        MergeMutationSettlementGuard guard = new();
        JObject baseline = PanelState(
            command == "chooseMergeFetter" ? "candidate" :
            command == "confirmMergeSettlement" ? "settlement" : "selection");
        Assert.True(guard.TryArm(
            Action(command, command == "chooseMergeFetter" ? new { index = 2 } : new { }),
            baseline,
            outcomeUnknown: true,
            now: 10f));

        MergeMutationSettlementStatus actual = guard.Observe(PanelState(phase), 11f, 20f);
        Assert.Equal(
            expectedSettled ? MergeMutationSettlementStatus.Settled : MergeMutationSettlementStatus.Waiting,
            actual);
    }

    [Fact]
    public void OpenAndCloseCommands_SettleOnlyOnTheirObservablePostconditions()
    {
        MergeMutationSettlementGuard open = new();
        Assert.True(open.TryArm(Action("openMergePanel"), ClosedState(), true, 1f));
        Assert.Equal(
            MergeMutationSettlementStatus.Waiting,
            open.Observe(ClosedState(), 1.5f, 20f));
        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            open.Observe(PanelState("selection"), 2f, 20f));

        MergeMutationSettlementGuard close = new();
        Assert.True(close.TryArm(Action("closeMergePanel"), PanelState("selection"), true, 1f));
        Assert.Equal(
            MergeMutationSettlementStatus.Settled,
            close.Observe(ClosedState(), 2f, 20f));
    }

    [Fact]
    public void UnsupportedCommandDoesNotArmAndResetAllowsANewMergeTransaction()
    {
        MergeMutationSettlementGuard guard = ArmedSelect(10f);

        guard.Reset();

        Assert.False(guard.IsArmed);
        Assert.Equal(MergeMutationSettlementStatus.None, guard.Status);
        Assert.False(guard.OutcomeUnknown);
        Assert.False(guard.TryArm(Action("queryMergeState"), SelectionState(false), false, 11f));
        Assert.True(guard.TryArm(Action("closeMergePanel"), PanelState("selection"), false, 11f));
    }

    private static MergeMutationSettlementGuard ArmedSelect(float now)
    {
        MergeMutationSettlementGuard guard = new();
        Assert.True(guard.TryArm(SelectVehicleAction(), SelectionState(false), true, now));
        return guard;
    }

    private static AutomationAction SelectVehicleAction() => Action(
        "selectMergeVehicle",
        new
        {
            index = 4,
            panelInstanceId = 700,
            rosterFingerprint = "roster-v1",
            itemInstanceId = 1004,
            vehicleInstanceId = 5004,
            materialVehicleType = "Shell_L1",
            resultVehicleType = "Shell_L2",
            requiredVehicleCount = 2,
            candidateVehicleIndexes = new[] { 4, 9 },
            candidateItemInstanceIds = new[] { 1004, 1009 },
            candidateVehicleInstanceIds = new[] { 5004, 5009 }
        });

    private static AutomationAction Action(string command, object? arguments = null) =>
        new(
            command,
            arguments == null ? new JObject() : JObject.FromObject(arguments),
            AutomationStage.PreparingDefense,
            "test");

    private static JObject SelectionState(
        bool selected,
        bool mergeOpen = true,
        int panelInstanceId = 700,
        string rosterFingerprint = "roster-v1",
        int selectedVehicleCount = 0)
    {
        if (!mergeOpen)
        {
            return ClosedState();
        }

        return Result(new
        {
            mergeOpen = true,
            phase = "selection",
            panelInstanceId,
            rosterFingerprint,
            selectedVehicleCount,
            mergeSelectedCount = selectedVehicleCount,
            mergeVehicles = new object[]
            {
                new
                {
                    index = 4,
                    instanceId = 1004,
                    selected,
                    vehicle = new { instanceId = 5004 }
                },
                new
                {
                    index = 9,
                    instanceId = 1009,
                    selected = false,
                    vehicle = new { instanceId = 5009 }
                }
            },
            mergeSubmitRule = new
            {
                requiredVehicleCount = 2,
                selectedVehicleCount,
                resultVehicleType = "Shell_L2",
                materialIndexes = new[] { 4, 9 },
                selectedVehicles = new[]
                {
                    new { instanceId = 5004 },
                    new { instanceId = 5009 }
                }
            }
        });
    }

    private static JObject PanelState(string phase, int selectedVehicleCount = 0) => Result(new
    {
        mergeOpen = true,
        phase,
        panelInstanceId = 700,
        rosterFingerprint = "roster-v1",
        selectedVehicleCount,
        mergeSelectedCount = selectedVehicleCount,
        mergeVehicles = Array.Empty<object>()
    });

    private static JObject ClosedState() => Result(new
    {
        mergeOpen = false,
        phase = "closed",
        panelInstanceId = 0,
        rosterFingerprint = string.Empty,
        selectedVehicleCount = 0,
        mergeVehicles = Array.Empty<object>()
    });

    private static JObject Result(object state) => new()
    {
        ["success"] = true,
        ["data"] = new JObject
        {
            ["state"] = JObject.FromObject(state)
        }
    };
}
