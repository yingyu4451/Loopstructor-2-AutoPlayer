using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MergeAutomationPlannerTests
{
    private readonly MergeAutomationPlanner _planner = new();

    [Fact]
    public void PotentialCandidate_UsesPlayerFormulaCountsAndExcludesNonMergeableVehicles()
    {
        JObject twoLevelOne = VehicleQuery(
            QueryVehicle("Shell_L1", 1),
            QueryVehicle("Shell_L1", 1));
        JObject twoLevelTwo = VehicleQuery(
            QueryVehicle("Shell_L2", 2),
            QueryVehicle("Shell_L2", 2));
        JObject threeLevelTwo = VehicleQuery(
            QueryVehicle("Shell_L2", 2),
            QueryVehicle("Shell_L2", 2),
            QueryVehicle("Shell_L2", 2));
        JObject excluded = VehicleQuery(
            QueryVehicle("Shell_L1", 1, isVirtual: true),
            QueryVehicle("Shell_L1", 1, isFixedHead: true),
            QueryVehicle("Shell_L3", 3),
            QueryVehicle("Shell_L3", 3),
            QueryVehicle("Shell_L3", 3));

        Assert.True(_planner.HasPotentialMergeCandidate(twoLevelOne));
        Assert.False(_planner.HasPotentialMergeCandidate(twoLevelTwo));
        Assert.True(_planner.HasPotentialMergeCandidate(threeLevelTwo));
        Assert.False(_planner.HasPotentialMergeCandidate(excluded));
    }

    [Fact]
    public void InitialFlow_OpensPanelThenQueriesState()
    {
        MergeAutomationDecision open = _planner.Decide(null, MergeAutomationState.Initial);

        AssertAction(open, "openMergePanel", MergeAutomationPhase.QuerySelectionState);

        MergeAutomationDecision query = _planner.Decide(null, open.NextState);

        AssertAction(query, "queryMergeState", MergeAutomationPhase.InspectSelectionState);
        Assert.Equal(AutomationStage.PreparingDefense, query.Action!.Stage);
    }

    [Fact]
    public void StableLegalGroup_SelectsOnlyFirstCandidateVehicleIndex()
    {
        JObject result = SelectionResult(
            Vehicle(1),
            Vehicle(4),
            Vehicle(9));
        AddGroup(result, "Shell_L1", "Shell_L2", 4, 9);
        MergeAutomationState inspect = new(MergeAutomationPhase.InspectSelectionState);

        MergeAutomationDecision firstObservation = _planner.Decide(result, inspect);
        AssertAction(firstObservation, "wait", MergeAutomationPhase.QuerySelectionState);

        MergeAutomationDecision secondObservation = ObserveAgain(result, firstObservation.NextState);

        AssertAction(secondObservation, "selectMergeVehicle", MergeAutomationPhase.InspectSelectionState);
        Assert.Equal(4, secondObservation.Action!.Arguments.Value<int>("index"));
        Assert.Equal(700, secondObservation.Action.Arguments.Value<int>("panelInstanceId"));
        Assert.Equal("roster-v1", secondObservation.Action.Arguments.Value<string>("rosterFingerprint"));
        Assert.Equal(1004, secondObservation.Action.Arguments.Value<int>("itemInstanceId"));
        Assert.Equal(5004, secondObservation.Action.Arguments.Value<int>("vehicleInstanceId"));
        Assert.Equal(new[] { 4, 9 }, secondObservation.Action.Arguments["candidateVehicleIndexes"]!.Values<int>());
        Assert.Equal(new[] { 1004, 1009 }, secondObservation.Action.Arguments["candidateItemInstanceIds"]!.Values<int>());
        Assert.Equal(new[] { 5004, 5009 }, secondObservation.Action.Arguments["candidateVehicleInstanceIds"]!.Values<int>());
        Assert.Equal(new[] { 4, 9 }, secondObservation.NextState.CandidateVehicleIndexes);
        Assert.Equal(new[] { 1004, 1009 }, secondObservation.NextState.CandidateItemInstanceIds);
        Assert.Equal(new[] { 5004, 5009 }, secondObservation.NextState.CandidateVehicleInstanceIds);
    }

    [Fact]
    public void PartialSelection_UsesRemainingPlannedCandidateInsteadOfAnotherVehicle()
    {
        JObject initial = SelectionResult(Vehicle(0), Vehicle(3), Vehicle(8));
        AddGroup(initial, "Link_L1", "Link_L2", 3, 8);
        MergeAutomationDecision first = _planner.Decide(
            initial,
            new MergeAutomationState(MergeAutomationPhase.InspectSelectionState));
        MergeAutomationDecision selectThree = ObserveAgain(initial, first.NextState);

        JObject partial = SelectionResult(Vehicle(0), Vehicle(3, selected: true), Vehicle(8));
        AddGroup(partial, "Link_L1", "Link_L2", 3, 8);
        SetSelectionCounts(partial, required: 2, selected: 1, ready: false);
        MergeAutomationDecision selectEight = _planner.Decide(partial, selectThree.NextState);

        AssertAction(selectEight, "selectMergeVehicle", MergeAutomationPhase.InspectSelectionState);
        Assert.Equal(8, selectEight.Action!.Arguments.Value<int>("index"));
        Assert.DoesNotContain(0, selectEight.NextState.CandidateVehicleIndexes);
    }

    [Fact]
    public void CompleteLegalSelection_SubmitsOnlyWhenAllPlayerRuleFlagsAgree()
    {
        JObject result = SelectionResult(Vehicle(2, true), Vehicle(5, true));
        AddGroup(result, "Gun_L1", "Gun_L2", 2, 5);
        SetSelectionCounts(result, required: 2, selected: 2, ready: true);
        MergeAutomationState inspect = new(
            MergeAutomationPhase.InspectSelectionState,
            new[] { 2, 5 });

        MergeAutomationDecision decision = _planner.Decide(result, inspect);

        AssertAction(decision, "submitMergeSelection", MergeAutomationPhase.QueryFetterOptions);
        Assert.Empty(decision.Action!.Arguments);
    }

    [Fact]
    public void ConflictingSubmitFlags_WaitsWithoutSubmitting()
    {
        JObject result = SelectionResult(Vehicle(2, true), Vehicle(5, true));
        AddGroup(result, "Gun_L1", "Gun_L2", 2, 5);
        SetSelectionCounts(result, required: 2, selected: 2, ready: true);
        State(result)["mergeReadyForSubmit"] = false;

        MergeAutomationDecision decision = _planner.Decide(
            result,
            new MergeAutomationState(
                MergeAutomationPhase.InspectSelectionState,
                new[] { 2, 5 },
                candidateVehicleInstanceIds: new[] { 5002, 5005 },
                candidateItemInstanceIds: new[] { 1002, 1005 },
                panelInstanceId: 700,
                rosterFingerprint: "roster-v1",
                materialVehicleType: "Gun_L1",
                resultVehicleType: "Gun_L2"));

        AssertAction(decision, "wait", MergeAutomationPhase.QuerySelectionState);
        Assert.Contains("尚未允许提交", decision.Detail);
    }

    [Fact]
    public void StableEmptyLegalGroups_CompletesWithoutWriting()
    {
        JObject result = SelectionResult(Vehicle(0));
        MergeAutomationDecision first = _planner.Decide(
            result,
            new MergeAutomationState(MergeAutomationPhase.InspectSelectionState));

        AssertAction(first, "wait", MergeAutomationPhase.QuerySelectionState);

        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        Assert.True(second.IsComplete);
        Assert.Null(second.Action);
        Assert.Contains("没有合法合成组", second.Detail);
        Assert.Equal(MergeAutomationCompletionKind.SafeEmptyPanel, second.CompletionKind);
    }

    [Fact]
    public void InvalidLegalGroup_CompletesWithoutInventingFallbackIndex()
    {
        JObject result = SelectionResult(Vehicle(0), Vehicle(1));
        JObject group = AddGroup(result, "Bad_L1", "Bad_L2", 0, 1);
        group["candidateVehicleIndexes"] = new JArray(0, 0);
        MergeAutomationDecision first = _planner.Decide(
            result,
            new MergeAutomationState(MergeAutomationPhase.InspectSelectionState));

        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        Assert.True(second.IsComplete);
        Assert.Null(second.Action);
        Assert.Contains("未伪造车辆索引", second.Detail);
        Assert.Equal(MergeAutomationCompletionKind.RecoveryRequired, second.CompletionKind);
    }

    [Fact]
    public void PlannedIndexDisappears_WaitsInsteadOfUsingCurrentListPosition()
    {
        JObject result = SelectionResult(Vehicle(0), Vehicle(4));
        AddGroup(result, "Shell_L1", "Shell_L2", 0, 4);
        MergeAutomationState inspect = new(
            MergeAutomationPhase.InspectSelectionState,
            new[] { 0, 9 });

        MergeAutomationDecision decision = _planner.Decide(result, inspect);

        AssertAction(decision, "wait", MergeAutomationPhase.QuerySelectionState);
        Assert.DoesNotContain("select", decision.Action!.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StableFetterOptions_PrefersExistingPrimaryFetter()
    {
        JObject result = OptionsResultForCandidates(
            new[] { 3, 8 },
            Option(7, 7007, "Poison - 2"),
            Option(2, 7002, "Slow - 1"));
        MergeAutomationState inspect = new(
            MergeAutomationPhase.InspectFetterOptions,
            new[] { 3, 8 },
            candidateVehicleInstanceIds: new[] { 5003, 5008 });

        MergeAutomationDecision first = _planner.Decide(result, inspect, new[] { "Poison" });
        AssertAction(first, "wait", MergeAutomationPhase.QueryFetterOptions);

        MergeAutomationDecision second = ObserveAgain(result, first.NextState, new[] { "Poison" });

        AssertAction(second, "chooseMergeFetter", MergeAutomationPhase.Completed);
        Assert.True(second.IsComplete);
        Assert.Equal(7, second.Action!.Arguments.Value<int>("index"));
    }

    [Fact]
    public void StableFetterOptions_WaitWhenFinalPlayerRuleNoLongerAllowsMerge()
    {
        JObject result = OptionsResult(Option(1, 101, "Poison - 1"));
        State(result)["canSubmitByPlayerRules"] = false;
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect);
        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        AssertAction(first, "wait", MergeAutomationPhase.QueryFetterOptions);
        AssertAction(second, "wait", MergeAutomationPhase.QueryFetterOptions);
        Assert.Contains("玩家合成规则尚未稳定", second.Detail);
        Assert.False(second.IsComplete);
    }

    [Fact]
    public void StableFetterOptions_WaitWhenSelectedVehicleIdentityChanged()
    {
        JObject result = OptionsResult(Option(1, 101, "Poison - 1"));
        ((JObject)((JArray)State(result).SelectToken("mergeSubmitRule.selectedVehicles")!)[1]!)["instanceId"] = 9999;
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect);
        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        AssertAction(first, "wait", MergeAutomationPhase.QueryFetterOptions);
        AssertAction(second, "wait", MergeAutomationPhase.QueryFetterOptions);
        Assert.Contains("素材对象身份", second.Detail);
        Assert.False(second.IsComplete);
    }

    [Fact]
    public void NoPrimaryFetterMatch_ChoosesSmallestValidRuntimeIndex()
    {
        JObject result = OptionsResult(
            Option(9, 9009, "Wind - 1"),
            Option(3, 9003, "Energy - 1"));
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect, new[] { "Poison" });
        MergeAutomationDecision second = ObserveAgain(result, first.NextState, new[] { "Poison" });

        AssertAction(second, "chooseMergeFetter", MergeAutomationPhase.Completed);
        Assert.Equal(3, second.Action!.Arguments.Value<int>("index"));
    }

    [Fact]
    public void DisabledPreferredOption_IsSkipped()
    {
        JObject disabled = Option(1, 8101, "Poison - 3");
        disabled["interactable"] = false;
        JObject result = OptionsResult(
            disabled,
            Option(6, 8106, "Energy - 1"));
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect, new[] { "Poison" });
        MergeAutomationDecision second = ObserveAgain(result, first.NextState, new[] { "Poison" });

        AssertAction(second, "chooseMergeFetter", MergeAutomationPhase.Completed);
        Assert.Equal(6, second.Action!.Arguments.Value<int>("index"));
    }

    [Fact]
    public void StableAllInvalidOptions_CompletesWithoutClicking()
    {
        JObject invalid = Option(4, 0, "None - 0");
        invalid["disabled"] = true;
        JObject result = OptionsResult(invalid);
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect);
        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        Assert.True(second.IsComplete);
        Assert.Null(second.Action);
        Assert.Contains("disabled/invalid", second.Detail);
    }

    [Fact]
    public void DuplicateRuntimeOptionIndexes_AreAllRejectedAsAmbiguous()
    {
        JObject result = OptionsResult(
            Option(4, 404, "Poison - 1"),
            Option(4, 405, "Energy - 1"));
        MergeAutomationState inspect = FetterInspectState();

        MergeAutomationDecision first = _planner.Decide(result, inspect);
        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        Assert.True(second.IsComplete);
        Assert.Null(second.Action);
        Assert.Contains("disabled/invalid", second.Detail);
    }

    [Fact]
    public void ChangedFetterOptions_RestartsStabilityObservation()
    {
        JObject firstResult = OptionsResult(Option(1, 101, "Wind - 1"));
        JObject changedResult = OptionsResult(Option(2, 202, "Energy - 1"));
        MergeAutomationDecision first = _planner.Decide(
            firstResult,
            FetterInspectState());
        MergeAutomationDecision query = _planner.Decide(null, first.NextState);

        MergeAutomationDecision changed = _planner.Decide(changedResult, query.NextState);

        AssertAction(changed, "wait", MergeAutomationPhase.QueryFetterOptions);
        Assert.False(changed.IsComplete);
    }

    [Fact]
    public void OptionsSeenDuringSelection_AreObservedBeforeAnyClick()
    {
        JObject result = OptionsResult(Option(5, 505, "Slow - 2"));
        MergeAutomationState inspect = FetterInspectState(MergeAutomationPhase.InspectSelectionState);

        MergeAutomationDecision decision = _planner.Decide(result, inspect);

        AssertAction(decision, "wait", MergeAutomationPhase.QueryFetterOptions);
    }

    [Fact]
    public void MissingLegalGroupsProperty_WaitsIndefinitelyInsteadOfTreatingItAsEmpty()
    {
        JObject result = SelectionResult(Vehicle(0));
        State(result).Property("legalMergeGroups")!.Remove();
        MergeAutomationState inspect = new(MergeAutomationPhase.InspectSelectionState);

        MergeAutomationDecision first = _planner.Decide(result, inspect);
        MergeAutomationDecision second = ObserveAgain(result, first.NextState);

        AssertAction(first, "wait", MergeAutomationPhase.QuerySelectionState);
        AssertAction(second, "wait", MergeAutomationPhase.QuerySelectionState);
        Assert.False(second.IsComplete);
    }

    private MergeAutomationDecision ObserveAgain(
        JObject result,
        MergeAutomationState queryState,
        IReadOnlyList<string>? primaryFetters = null)
    {
        MergeAutomationDecision query = _planner.Decide(null, queryState, primaryFetters);
        Assert.Equal("queryMergeState", query.Action?.Command);
        return _planner.Decide(result, query.NextState, primaryFetters);
    }

    private static JObject SelectionResult(params JObject[] vehicles) => JObject.FromObject(new
    {
        data = new
        {
            state = new
            {
                mergeOpen = true,
                panelInstanceId = 700,
                rosterFingerprint = "roster-v1",
                mergeSelectedCount = 0,
                mergeVehicles = vehicles,
                requiredVehicleCount = 0,
                selectedVehicleCount = 0,
                canSubmitByPlayerRules = false,
                blockers = Array.Empty<string>(),
                legalMergeGroups = Array.Empty<object>(),
                mergeSubmitRule = new
                {
                    requiredVehicleCount = 0,
                    selectedVehicleCount = 0,
                    canSubmitByPlayerRules = false,
                    blockers = Array.Empty<string>(),
                    formulaMatched = false,
                    resultVehicleType = (string?)null,
                    materialIndexes = Array.Empty<int>(),
                    selectedVehicles = Array.Empty<object>()
                },
                mergeReadyForSubmit = false,
                mergeOptions = Array.Empty<object>()
            }
        }
    });

    private static JObject OptionsResult(params JObject[] options)
    {
        return OptionsResultForCandidates(new[] { 0, 1 }, options);
    }

    private static JObject OptionsResultForCandidates(int[] candidateIndexes, params JObject[] options)
    {
        JObject result = SelectionResult(candidateIndexes.Select(index => Vehicle(index, selected: true)).ToArray());
        SetSelectionCounts(result, candidateIndexes.Length, candidateIndexes.Length, ready: true);
        JObject state = State(result);
        JObject rule = (JObject)state["mergeSubmitRule"]!;
        rule["materialIndexes"] = new JArray(candidateIndexes.Cast<object>().ToArray());
        rule["blockers"] = new JArray();
        rule["selectedVehicles"] = new JArray(candidateIndexes.Select(index =>
            JObject.FromObject(new { instanceId = 5000 + index })));
        state["mergeOptions"] = new JArray(options);
        return result;
    }

    private static MergeAutomationState FetterInspectState(
        MergeAutomationPhase phase = MergeAutomationPhase.InspectFetterOptions) =>
        new(
            phase,
            new[] { 0, 1 },
            candidateVehicleInstanceIds: new[] { 5000, 5001 });

    private static JObject Vehicle(int index, bool selected = false) => JObject.FromObject(new
    {
        index,
        instanceId = 1000 + index,
        path = $"Canvas/Merge/Vehicle{index}",
        selected,
        vehicle = new
        {
            instanceId = 5000 + index,
            name = $"Vehicle{index}",
            vehicleType = "Shell_L1",
            level = 1
        }
    });

    private static JObject VehicleQuery(params JObject[] vehicles) => new()
    {
        ["success"] = true,
        ["data"] = new JObject
        {
            ["state"] = new JObject { ["vehicles"] = new JArray(vehicles) }
        }
    };

    private static JObject QueryVehicle(
        string vehicleType,
        int level,
        bool isVirtual = false,
        bool isFixedHead = false) => JObject.FromObject(new
    {
        vehicleType,
        level,
        isVirtual,
        isFixedHead
    });

    private static JObject AddGroup(
        JObject result,
        string materialType,
        string resultType,
        params int[] candidateIndexes)
    {
        JObject group = JObject.FromObject(new
        {
            materialVehicleType = materialType,
            resultVehicleType = resultType,
            requiredVehicleCount = candidateIndexes.Length,
            availableCount = candidateIndexes.Length,
            selectedVehicleCount = candidateIndexes.Length,
            canSubmit = true,
            candidateVehicleIndexes = candidateIndexes
        });
        ((JArray)State(result)["legalMergeGroups"]!).Add(group);
        return group;
    }

    private static JObject Option(int index, int instanceId, string fetter) => JObject.FromObject(new
    {
        index,
        instanceId,
        type = "RebuildUI_Option_Merge",
        path = $"Canvas/Merge/Option{index}",
        fetter
    });

    private static void SetSelectionCounts(
        JObject result,
        int required,
        int selected,
        bool ready)
    {
        JObject state = State(result);
        state["mergeSelectedCount"] = selected;
        state["requiredVehicleCount"] = required;
        state["selectedVehicleCount"] = selected;
        state["canSubmitByPlayerRules"] = ready;
        state["mergeReadyForSubmit"] = ready;
        JObject rule = (JObject)state["mergeSubmitRule"]!;
        rule["requiredVehicleCount"] = required;
        rule["selectedVehicleCount"] = selected;
        rule["canSubmitByPlayerRules"] = ready;
        rule["formulaMatched"] = ready;
    }

    private static JObject State(JObject result) => (JObject)result.SelectToken("data.state")!;

    private static void AssertAction(
        MergeAutomationDecision decision,
        string command,
        MergeAutomationPhase nextPhase)
    {
        Assert.NotNull(decision.Action);
        Assert.Equal(command, decision.Action!.Command);
        Assert.Equal(nextPhase, decision.NextState.Phase);
    }
}
