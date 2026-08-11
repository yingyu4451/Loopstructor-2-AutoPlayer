using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningDefensePreparationPlannerTests
{
    [Fact]
    public void GridRanking_PrefersRepairingDirectionalCoverageBeforeNearestAnchorDistance()
    {
        OpeningDefenseGrid[] anchors =
        {
            new(0, 0),
            new(4, 0),
            new(20, 20)
        };
        OpeningDefenseGrid[] candidates =
        {
            new(8, 8),
            new(2, 1),
            new(2, -1),
            new(2, 1)
        };

        IReadOnlyList<OpeningDefenseGrid> ranked = OpeningDefenseGridRanker.Rank(candidates, anchors);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(new OpeningDefenseGrid(2, -1), ranked[0]);
        Assert.Equal(new OpeningDefenseGrid(8, 8), ranked[1]);
        Assert.Equal(new OpeningDefenseGrid(2, 1), ranked[2]);
    }

    [Fact]
    public void ExistingAttribute_PreparesAcrossFrames_WithStableIdentitiesAndNoMacro()
    {
        OpeningDefensePreparationPlanner planner = new(new FakeProbe());
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        ObserveNext(planner, "queryVehicle", VehicleResult(BagVehicle(500, level: 3)));

        OpeningDefensePreparationDecision preview = planner.Decide();
        AssertAction(preview, "previewRailPath");
        Assert.Equal(new[] { 100, 101, 102 }, preview.Action!.Arguments["linePointInstanceIds"]!.Values<int>());
        Assert.Equal(500, preview.Action.Arguments.SelectToken("vehicle.instanceId")?.Value<int>());
        Assert.Equal(500, preview.Action.Arguments["vehicleInstanceId"]?.Value<int>());
        planner.Observe(preview.Action, PreviewSuccess(), accepted: true);

        ObserveNext(planner, "queryRail", RailResult());
        OpeningDefensePreparationDecision draw = planner.Decide();
        AssertAction(draw, "drawRailPath");
        planner.Observe(draw.Action!, DrawResult(CommittedRail()), accepted: true);

        ObserveNext(planner, "queryRail", RailResult(CommittedRail()));
        OpeningDefensePreparationDecision placementTrain = planner.Decide();
        AssertAction(placementTrain, "queryTrain");
        planner.Observe(
            placementTrain.Action!,
            TrainResult(Train(railId: 77, FixedHead(900))),
            accepted: true);

        OpeningDefensePreparationDecision placement = planner.Decide();
        AssertAction(placement, "moveVehicleInTrain");
        Assert.Equal(500, placement.Action!.Arguments["instanceId"]!.Value<int>());
        Assert.Equal(900, placement.Action.Arguments.SelectToken("relative.instanceId")!.Value<int>());
        planner.Observe(placement.Action, Success(), accepted: true);

        ObserveNext(
            planner,
            "queryTrain",
            TrainResult(Train(railId: 77, FixedHead(900), FieldVehicle(500, railId: 77))));
        ObserveNext(planner, "queryVehicle", VehicleResult(FieldVehicle(500, railId: 77)));
        ObserveNext(planner, "queryRail", RailResult(CommittedRail()));

        OpeningDefensePreparationDecision complete = planner.Decide();
        Assert.True(complete.IsComplete);
        Assert.Null(complete.Action);
        Assert.Equal(500, planner.SelectedVehicleInstanceId);
        Assert.Equal(700, planner.VerifiedRailInstanceId);
        Assert.True(planner.DrawSubmitted);
        Assert.True(planner.HasCommittedWrite);
        Assert.Equal("moveVehicleInTrain", planner.VehiclePlacementCommand);
        Assert.DoesNotContain("prepareDefaultDefense", AllCommands(planner), StringComparer.Ordinal);

        planner.ResumeCommittedTransaction();

        Assert.Equal(OpeningDefensePreparationPhase.Completed, planner.Phase);
        Assert.True(planner.Decide().IsComplete);
    }

    [Fact]
    public void MissingAttribute_UsesIncrementalPlacementBeforeConstruction()
    {
        FakeProbe probe = new(
            new OpeningDefenseGridProbeResult(
                OpeningDefenseGridProbeStatus.Found,
                new OpeningDefenseGrid(0, 2),
                totalProbed: 3));
        OpeningDefensePreparationPlanner planner = new(probe);

        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(Common(101, -2, -1), Common(102, 2, -1)));
        ObserveNext(
            planner,
            "queryOpeningDefenseInteractionGuard",
            InteractionGuardResult(noActiveInteraction: true));
        OpeningDefensePreparationDecision confirm = planner.Decide();
        AssertAction(confirm, "confirmDisposableGrid");
        Assert.Equal(0, confirm.Action!.Arguments.SelectToken("grid.x")!.Value<int>());
        Assert.Equal(2, confirm.Action.Arguments.SelectToken("grid.y")!.Value<int>());
        planner.Observe(confirm.Action, Success(), accepted: true);

        Assert.True(planner.HasCommittedWrite);
        AssertAction(planner.Decide(), "wait");
        planner.MarkPlacementPreviewReleased();
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));

        Assert.Equal(OpeningDefensePreparationPhase.QueryVehicle, planner.Phase);
        Assert.Equal(1, probe.InitializeCalls);
        Assert.Equal(1, probe.ProbeCalls);
        Assert.Equal(2, probe.LastAnchors.Count);
    }

    [Fact]
    public void ResumeCommittedTransaction_AfterAttributeConfirm_NeverConfirmsGridAgain()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtInteractionGuard();
        ObserveNext(
            planner,
            "queryOpeningDefenseInteractionGuard",
            InteractionGuardResult(noActiveInteraction: true));
        OpeningDefensePreparationDecision confirm = planner.Decide();
        AssertAction(confirm, "confirmDisposableGrid");
        planner.Observe(confirm.Action!, Success(), accepted: true);

        Assert.True(planner.HasCommittedWrite);
        Assert.Equal(OpeningDefensePreparationPhase.WaitForPlacementSettlement, planner.Phase);

        planner.ResumeCommittedTransaction();

        Assert.Equal(OpeningDefensePreparationPhase.WaitForPlacementSettlement, planner.Phase);
        AssertAction(planner.Decide(), "wait");
        planner.MarkPlacementPreviewReleased();
        planner.ResumeCommittedTransaction();

        Assert.Equal(OpeningDefensePreparationPhase.VerifyAttributePlacement, planner.Phase);
        AssertAction(planner.Decide(), "queryCatapults");
        Assert.DoesNotContain("confirmDisposableGrid", AllCommands(planner), StringComparer.Ordinal);

        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(999, 1, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        Assert.Equal(OpeningDefensePreparationPhase.VerifyAttributePlacement, planner.Phase);

        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        Assert.Equal(OpeningDefensePreparationPhase.QueryVehicle, planner.Phase);
    }

    [Fact]
    public void MissingAttribute_WaitsForExplicitlyClearInteractionGuardBeforeConfirming()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtInteractionGuard();

        OpeningDefensePreparationDecision guard = planner.Decide();
        AssertAction(guard, "queryOpeningDefenseInteractionGuard");
        planner.Observe(
            guard.Action!,
            InteractionGuardResult(noActiveInteraction: false),
            accepted: true);

        Assert.Equal(OpeningDefensePreparationPhase.QueryInteractionGuard, planner.Phase);
        AssertAction(planner.Decide(), "queryOpeningDefenseInteractionGuard");
        ObserveNext(
            planner,
            "queryOpeningDefenseInteractionGuard",
            InteractionGuardResult(noActiveInteraction: true));

        Assert.Equal(OpeningDefensePreparationPhase.ConfirmAttributeGrid, planner.Phase);
        AssertAction(planner.Decide(), "confirmDisposableGrid");
    }

    [Fact]
    public void InteractionGuard_MalformedSuccessFailsClosedBeforeConfirming()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtInteractionGuard();
        OpeningDefensePreparationDecision guard = planner.Decide();

        planner.Observe(guard.Action!, Result(new JObject()), accepted: true);

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "缺少明确");
        Assert.DoesNotContain("confirmDisposableGrid", AllCommands(planner), StringComparer.Ordinal);
    }

    [Fact]
    public void PreWriteReadFailures_RetryInPlaceAndThenAcceptValidState()
    {
        OpeningDefensePreparationPlanner catapults = new(new FakeProbe());
        AssertTransientReadThenSuccess(
            catapults,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)),
            OpeningDefensePreparationPhase.QueryVehicle);

        OpeningDefensePreparationPlanner vehicle = new(new FakeProbe());
        ObserveNext(
            vehicle,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        AssertTransientReadThenSuccess(
            vehicle,
            "queryVehicle",
            VehicleResult(BagVehicle(500, level: 3)),
            OpeningDefensePreparationPhase.PreviewRailPath);

        OpeningDefensePreparationPlanner preview = ReadyAtRailPreview();
        AssertTransientReadThenSuccess(
            preview,
            "previewRailPath",
            PreviewSuccess(),
            OpeningDefensePreparationPhase.QueryRailBaseline);

        OpeningDefensePreparationPlanner baseline = ReadyAtRailBaseline();
        AssertTransientReadThenSuccess(
            baseline,
            "queryRail",
            RailResult(),
            OpeningDefensePreparationPhase.DrawRailPath);
    }

    [Fact]
    public void PreWriteReadFailures_StopAtBoundWithoutSubmittingWrite()
    {
        OpeningDefensePreparationPlanner planner = new(new FakeProbe());

        for (int attempt = 0; attempt < 12; attempt++)
        {
            OpeningDefensePreparationDecision query = planner.Decide();
            AssertAction(query, "queryCatapults");
            planner.Observe(query.Action!, Failure(), accepted: false);
        }

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "12 次上限");
        Assert.False(planner.DrawSubmitted);
        Assert.DoesNotContain("drawRailPath", AllCommands(planner), StringComparer.Ordinal);
    }

    [Fact]
    public void SuccessfulMalformedPreWriteStateFailsImmediatelyInsteadOfRetrying()
    {
        OpeningDefensePreparationPlanner planner = new(new FakeProbe());
        OpeningDefensePreparationDecision query = planner.Decide();

        planner.Observe(query.Action!, Result(new JObject()), accepted: true);

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "缺少 catapults 数组");
    }

    [Fact]
    public void EmptyVerifiedRail_UsesPlaceVehicleOnLineOnlyAfterTrainQueryProvesNoDriver()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();
        JObject emptyRail = CommittedRail(hasDriver: false);
        ObserveNext(planner, "queryRail", RailResult(emptyRail));
        ObserveNext(planner, "queryTrain", TrainResult());

        OpeningDefensePreparationDecision placement = planner.Decide();

        AssertAction(placement, "placeVehicleOnLine");
        Assert.Equal(500, placement.Action!.Arguments["instanceId"]!.Value<int>());
        Assert.Equal(800, placement.Action.Arguments["lineInstanceId"]!.Value<int>());
    }

    [Fact]
    public void DriverWithoutQueryableTrain_NeverFallsBackToPlaceVehicleOnLine()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();
        ObserveNext(planner, "queryRail", RailResult(CommittedRail(hasDriver: true)));

        for (int attempt = 0; attempt < 12; attempt++)
        {
            OpeningDefensePreparationDecision query = planner.Decide();
            AssertAction(query, "queryTrain");
            planner.Observe(query.Action!, TrainResult(), accepted: true);
        }

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "不会错误调用 placeVehicleOnLine");
        Assert.DoesNotContain("placeVehicleOnLine", AllCommands(planner), StringComparer.Ordinal);
    }

    [Fact]
    public void DrawFailure_IsReconciledByQueriesAndIsNeverResubmitted()
    {
        OpeningDefensePreparationPlanner planner = ReadyToDraw();
        OpeningDefensePreparationDecision draw = planner.Decide();
        AssertAction(draw, "drawRailPath");
        planner.Observe(draw.Action!, Failure(), accepted: false);

        for (int attempt = 0; attempt < 12; attempt++)
        {
            OpeningDefensePreparationDecision query = planner.Decide();
            AssertAction(query, "queryRail");
            planner.Observe(query.Action!, RailResult(), accepted: true);
        }

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "拒绝重画");
        Assert.True(planner.DrawSubmitted);
        Assert.NotEqual("drawRailPath", failed.Action!.Command);
    }

    [Fact]
    public void MultipleNewRails_FailsImmediatelyWithoutDeletionOrRedraw()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();

        ObserveNext(
            planner,
            "queryRail",
            RailResult(CommittedRail(), OtherRail()));

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "已保留现场");
        Assert.DoesNotContain("drawRailPath", AllCommands(planner), StringComparer.Ordinal);
    }

    [Fact]
    public void FinalVerification_IsBoundedAndNeverResendsPlacement()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtPlacement();
        OpeningDefensePreparationDecision placement = planner.Decide();
        AssertAction(placement, "moveVehicleInTrain");
        planner.Observe(placement.Action!, Failure(), accepted: false);

        for (int attempt = 0; attempt < 12; attempt++)
        {
            OpeningDefensePreparationDecision query = planner.Decide();
            AssertAction(query, "queryTrain");
            planner.Observe(query.Action!, TrainResult(Train(77, FixedHead(900))), accepted: true);
        }

        OpeningDefensePreparationDecision failed = planner.Decide();
        AssertTerminalFailure(failed, "不会重发写命令");
        Assert.True(planner.DrawSubmitted);
        Assert.Equal("moveVehicleInTrain", planner.VehiclePlacementCommand);
    }

    [Fact]
    public void ProbeInitializationFailure_StopsWithoutCompatibilityFallback()
    {
        FakeProbe probe = new() { InitializeSucceeds = false };
        OpeningDefensePreparationPlanner planner = new(probe);
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(Common(101, -2, -1), Common(102, 2, -1)));

        OpeningDefensePreparationDecision failure = planner.Decide();

        AssertTerminalFailure(failure, "无法初始化增量候选网格探测");
        Assert.False(failure.UsesLegacyFallback);
    }

    [Fact]
    public void Reset_ClearsSubmittedIdentitiesAndReturnsToFirstQuery()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();
        Assert.True(planner.DrawSubmitted);
        Assert.Equal(500, planner.SelectedVehicleInstanceId);

        planner.Reset();

        Assert.False(planner.DrawSubmitted);
        Assert.False(planner.HasCommittedWrite);
        Assert.Equal(0, planner.SelectedVehicleInstanceId);
        Assert.Equal(0, planner.VerifiedRailInstanceId);
        Assert.Equal(OpeningDefensePreparationPhase.QueryCatapults, planner.Phase);
        AssertAction(planner.Decide(), "queryCatapults");
    }

    [Fact]
    public void ResumeCommittedTransaction_AfterStopFollowingDraw_ReconcilesRailWithoutRedrawing()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();
        int selectedVehicleInstanceId = planner.SelectedVehicleInstanceId;
        int[] linePointInstanceIds = planner.SelectedLinePointInstanceIds.ToArray();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            ObserveNext(planner, "queryRail", RailResult());
        }

        planner.ResumeCommittedTransaction();

        Assert.True(planner.HasCommittedWrite);
        Assert.True(planner.DrawSubmitted);
        Assert.Equal(selectedVehicleInstanceId, planner.SelectedVehicleInstanceId);
        Assert.Equal(linePointInstanceIds, planner.SelectedLinePointInstanceIds);
        Assert.Equal(OpeningDefensePreparationPhase.VerifyRail, planner.Phase);
        AssertAction(planner.Decide(), "queryRail");

        for (int attempt = 0; attempt < 11; attempt++)
        {
            ObserveNext(planner, "queryRail", RailResult());
        }

        Assert.Equal(OpeningDefensePreparationPhase.VerifyRail, planner.Phase);
        ObserveNext(planner, "queryRail", RailResult(CommittedRail()));
        Assert.Equal(OpeningDefensePreparationPhase.QueryPlacementTrain, planner.Phase);
        Assert.NotEqual("drawRailPath", planner.Decide().Action!.Command);
    }

    [Fact]
    public void ResumeCommittedTransaction_AfterPlacementFault_ReconcilesTrainWithoutReplacingVehicle()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtPlacement();
        OpeningDefensePreparationDecision placement = planner.Decide();
        AssertAction(placement, "moveVehicleInTrain");
        planner.Observe(placement.Action!, Failure(), accepted: false);

        for (int attempt = 0; attempt < 12; attempt++)
        {
            ObserveNext(planner, "queryTrain", TrainResult(Train(77, FixedHead(900))));
        }

        Assert.Equal(OpeningDefensePreparationPhase.PlacementVerificationFailed, planner.Phase);
        int selectedVehicleInstanceId = planner.SelectedVehicleInstanceId;
        int verifiedRailInstanceId = planner.VerifiedRailInstanceId;
        string placementCommand = planner.VehiclePlacementCommand;

        planner.ResumeCommittedTransaction();

        Assert.True(planner.HasCommittedWrite);
        Assert.Equal(selectedVehicleInstanceId, planner.SelectedVehicleInstanceId);
        Assert.Equal(verifiedRailInstanceId, planner.VerifiedRailInstanceId);
        Assert.Equal(placementCommand, planner.VehiclePlacementCommand);
        Assert.Equal(OpeningDefensePreparationPhase.VerifyTrain, planner.Phase);
        AssertAction(planner.Decide(), "queryTrain");

        for (int attempt = 0; attempt < 11; attempt++)
        {
            ObserveNext(planner, "queryTrain", TrainResult(Train(77, FixedHead(900))));
        }

        Assert.Equal(OpeningDefensePreparationPhase.VerifyTrain, planner.Phase);
        ObserveNext(
            planner,
            "queryTrain",
            TrainResult(Train(77, FixedHead(900), FieldVehicle(500, railId: 77))));
        Assert.Equal(OpeningDefensePreparationPhase.VerifyVehicle, planner.Phase);
        Assert.NotEqual("moveVehicleInTrain", planner.Decide().Action!.Command);
        Assert.NotEqual("placeVehicleOnLine", planner.Decide().Action!.Command);
    }

    [Fact]
    public void ResumeCommittedTransaction_WithoutCommittedWrite_UsesResetSemantics()
    {
        FakeProbe probe = new();
        OpeningDefensePreparationPlanner planner = new(probe);
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        Assert.False(planner.HasCommittedWrite);

        planner.ResumeCommittedTransaction();

        Assert.Equal(1, probe.ResetCalls);
        Assert.False(planner.HasCommittedWrite);
        Assert.Equal(0, planner.SelectedVehicleInstanceId);
        Assert.Equal(OpeningDefensePreparationPhase.QueryCatapults, planner.Phase);
        AssertAction(planner.Decide(), "queryCatapults");
    }

    private static OpeningDefensePreparationPlanner ReadyToDraw()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailBaseline();
        ObserveNext(planner, "queryRail", RailResult());
        Assert.Equal(OpeningDefensePreparationPhase.DrawRailPath, planner.Phase);
        return planner;
    }

    private static OpeningDefensePreparationPlanner ReadyAtRailPreview()
    {
        OpeningDefensePreparationPlanner planner = new(new FakeProbe());
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(
                Attribute(100, 0, 2),
                Common(101, -2, -1),
                Common(102, 2, -1)));
        ObserveNext(planner, "queryVehicle", VehicleResult(BagVehicle(500, level: 3)));
        Assert.Equal(OpeningDefensePreparationPhase.PreviewRailPath, planner.Phase);
        return planner;
    }

    private static OpeningDefensePreparationPlanner ReadyAtRailBaseline()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailPreview();
        ObserveNext(planner, "previewRailPath", PreviewSuccess());
        Assert.Equal(OpeningDefensePreparationPhase.QueryRailBaseline, planner.Phase);
        return planner;
    }

    private static OpeningDefensePreparationPlanner ReadyAtInteractionGuard()
    {
        FakeProbe probe = new(
            new OpeningDefenseGridProbeResult(
                OpeningDefenseGridProbeStatus.Found,
                new OpeningDefenseGrid(0, 2),
                totalProbed: 1));
        OpeningDefensePreparationPlanner planner = new(probe);
        ObserveNext(
            planner,
            "queryCatapults",
            CatapultResult(Common(101, -2, -1), Common(102, 2, -1)));
        AssertAction(planner.Decide(), "queryOpeningDefenseInteractionGuard");
        Assert.Equal(OpeningDefensePreparationPhase.QueryInteractionGuard, planner.Phase);
        return planner;
    }

    private static OpeningDefensePreparationPlanner ReadyAtRailVerification()
    {
        OpeningDefensePreparationPlanner planner = ReadyToDraw();
        OpeningDefensePreparationDecision draw = planner.Decide();
        planner.Observe(draw.Action!, DrawResult(CommittedRail()), accepted: true);
        Assert.Equal(OpeningDefensePreparationPhase.VerifyRail, planner.Phase);
        return planner;
    }

    private static OpeningDefensePreparationPlanner ReadyAtPlacement()
    {
        OpeningDefensePreparationPlanner planner = ReadyAtRailVerification();
        ObserveNext(planner, "queryRail", RailResult(CommittedRail()));
        ObserveNext(planner, "queryTrain", TrainResult(Train(77, FixedHead(900))));
        Assert.Equal(OpeningDefensePreparationPhase.PlaceVehicle, planner.Phase);
        return planner;
    }

    private static void ObserveNext(
        OpeningDefensePreparationPlanner planner,
        string expectedCommand,
        JObject result,
        bool accepted = true)
    {
        OpeningDefensePreparationDecision decision = planner.Decide();
        AssertAction(decision, expectedCommand);
        planner.Observe(decision.Action!, result, accepted);
    }

    private static void AssertTransientReadThenSuccess(
        OpeningDefensePreparationPlanner planner,
        string expectedCommand,
        JObject successfulResult,
        OpeningDefensePreparationPhase expectedPhase)
    {
        OpeningDefensePreparationPhase originalPhase = planner.Phase;
        OpeningDefensePreparationDecision first = planner.Decide();
        AssertAction(first, expectedCommand);
        planner.Observe(first.Action!, Failure(), accepted: false);
        Assert.Equal(originalPhase, planner.Phase);

        ObserveNext(planner, expectedCommand, successfulResult);
        Assert.Equal(expectedPhase, planner.Phase);
    }

    private static IEnumerable<string> AllCommands(OpeningDefensePreparationPlanner planner)
    {
        OpeningDefensePreparationDecision decision = planner.Decide();
        if (decision.Action != null) yield return decision.Action.Command;
        yield return planner.VehiclePlacementCommand;
    }

    private static JObject CatapultResult(params JObject[] catapults) =>
        Result(new JObject { ["catapults"] = new JArray(catapults) });

    private static JObject Common(int instanceId, int x, int y) =>
        Catapult(instanceId, x, y, isAttribute: false);

    private static JObject Attribute(int instanceId, int x, int y) =>
        Catapult(instanceId, x, y, isAttribute: true);

    private static JObject Catapult(int instanceId, int x, int y, bool isAttribute) =>
        JObject.FromObject(new
        {
            active = true,
            canUseForNewRail = true,
            canPickLine = true,
            frozen = false,
            railReachMax = false,
            railMembershipCount = 0,
            linePointInstanceId = instanceId,
            isAttribute,
            grid = new { x, y }
        });

    private static JObject VehicleResult(params JObject[] vehicles) =>
        Result(new JObject { ["vehicles"] = new JArray(vehicles) });

    private static JObject BagVehicle(int instanceId, int level) =>
        JObject.FromObject(new
        {
            instanceId,
            index = 0,
            level,
            inBag = true,
            active = false,
            isFixedHead = false
        });

    private static JObject FixedHead(int instanceId) =>
        JObject.FromObject(new
        {
            instanceId,
            inBag = false,
            active = true,
            isFixedHead = true
        });

    private static JObject FieldVehicle(int instanceId, int railId) =>
        JObject.FromObject(new
        {
            instanceId,
            railId,
            inBag = false,
            active = true,
            isFixedHead = false
        });

    private static JObject PreviewSuccess() =>
        Result(new JObject
        {
            ["wouldBeLegal"] = true,
            ["sideEffectCheckPassed"] = true,
            ["statePolluted"] = false,
            ["requiresSpeedSource"] = false,
            ["predictedLoopCycleSeconds"] = 8.5d,
            ["beforeRailCount"] = 0,
            ["afterRailCount"] = 0
        });

    private static JObject InteractionGuardResult(bool noActiveInteraction) =>
        Result(new JObject
        {
            ["noActiveInteraction"] = noActiveInteraction,
            ["observationConsistent"] = true,
            ["isInPreview"] = !noActiveInteraction,
            ["hasLastInteraction"] = !noActiveInteraction
        });

    private static JObject DrawResult(JObject rail) =>
        Result(new JObject { ["rail"] = rail.DeepClone() });

    private static JObject RailResult(params JObject[] rails) =>
        Result(new JObject
        {
            ["railCount"] = rails.Length,
            ["rails"] = new JArray(rails.Select(rail => rail.DeepClone()))
        });

    private static JObject CommittedRail(bool hasDriver = true) =>
        JObject.FromObject(new
        {
            instanceId = 700,
            railInternalId = 77,
            id = 77,
            isLegalPlayerLoop = true,
            isLoop = true,
            isOnField = true,
            points = new[]
            {
                new { instanceId = 100 },
                new { instanceId = 101 },
                new { instanceId = 102 }
            },
            lines = new[]
            {
                new
                {
                    lineInstanceId = 800,
                    hasDriver,
                    driverCount = hasDriver ? 1 : 0
                }
            }
        });

    private static JObject OtherRail() =>
        JObject.FromObject(new
        {
            instanceId = 701,
            railInternalId = 78,
            id = 78,
            isLegalPlayerLoop = true,
            isLoop = true,
            isOnField = true,
            points = new[]
            {
                new { instanceId = 201 },
                new { instanceId = 202 },
                new { instanceId = 203 }
            },
            lines = Array.Empty<object>()
        });

    private static JObject TrainResult(params JObject[] trains) =>
        Result(new JObject { ["trains"] = new JArray(trains) });

    private static JObject Train(int railId, params JObject[] vehicles) =>
        JObject.FromObject(new
        {
            index = 0,
            railId,
            realVehicleCount = vehicles.Count(vehicle => vehicle["isFixedHead"]?.Value<bool>() != true),
            capacity = 5,
            isOverCapacity = false,
            vehicles
        });

    private static JObject Result(JObject state) =>
        new()
        {
            ["success"] = true,
            ["data"] = new JObject { ["state"] = state }
        };

    private static JObject Success() => JObject.FromObject(new { success = true });
    private static JObject Failure() => JObject.FromObject(new { success = false });

    private static void AssertAction(OpeningDefensePreparationDecision decision, string command)
    {
        Assert.NotNull(decision.Action);
        Assert.Equal(command, decision.Action!.Command);
        Assert.Equal(AutomationStage.PreparingDefense, decision.Action.Stage);
    }

    private static void AssertTerminalFailure(
        OpeningDefensePreparationDecision decision,
        string expectedDetail)
    {
        Assert.Equal(OpeningDefensePreparationPhase.PlacementVerificationFailed, decision.Phase);
        AssertAction(decision, "wait");
        Assert.False(decision.UsesLegacyFallback);
        Assert.False(decision.IsComplete);
        Assert.Contains(expectedDetail, decision.Detail, StringComparison.Ordinal);
    }

    private sealed class FakeProbe : IOpeningDefenseGridProbe
    {
        private readonly Queue<OpeningDefenseGridProbeResult> _results;

        public FakeProbe(params OpeningDefenseGridProbeResult[] results)
        {
            _results = new Queue<OpeningDefenseGridProbeResult>(results);
        }

        public bool InitializeSucceeds { get; set; } = true;
        public int InitializeCalls { get; private set; }
        public int ProbeCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public IReadOnlyList<OpeningDefenseGrid> LastAnchors { get; private set; } =
            Array.Empty<OpeningDefenseGrid>();

        public bool TryInitialize(IReadOnlyList<OpeningDefenseGrid> commonPointAnchors, out string error)
        {
            InitializeCalls++;
            LastAnchors = commonPointAnchors.ToArray();
            error = InitializeSucceeds ? string.Empty : "unavailable";
            return InitializeSucceeds;
        }

        public OpeningDefenseGridProbeResult ProbeNext()
        {
            ProbeCalls++;
            return _results.Count > 0
                ? _results.Dequeue()
                : new OpeningDefenseGridProbeResult(
                    OpeningDefenseGridProbeStatus.Exhausted,
                    totalProbed: ProbeCalls);
        }

        public void Reset() => ResetCalls++;
    }
}
