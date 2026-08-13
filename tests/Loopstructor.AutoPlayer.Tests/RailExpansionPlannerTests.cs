using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailExpansionPlannerTests
{
    private readonly RailExpansionPlanner _planner = new();

    [Fact]
    public void BuildCandidates_OrdersByCurrentTriggerRateAndCarriesDeterministicVehicleIdentity()
    {
        JObject rails = Result(new
        {
            rails = new[]
            {
                Rail(701, 71, stationCount: 4, cycle: 8d, lineId: 801, pointIds: new[] { 11, 12, 13, 14 }),
                Rail(702, 72, stationCount: 3, cycle: 3d, lineId: 802, pointIds: new[] { 21, 22, 23 })
            }
        });
        JObject catapults = Result(new { catapults = new[] { UnusedStation(301, 401, 501) } });
        JObject trains = Result(new
        {
            trains = new[]
            {
                Train(72, index: 4, Vehicle(904, index: 0, isTrainHead: true)),
                Train(72, index: 1, Vehicle(901, index: 2), Vehicle(900, index: 0, isTrainHead: true)),
                Train(71, index: 0, Vehicle(800, index: 0, isTrainHead: true))
            }
        });

        IReadOnlyList<RailInsertionCandidate> candidates = _planner.BuildCandidates(rails, catapults, trains);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(72, candidates[0].RailInternalId);
        Assert.Equal(900, candidates[0].VehicleInstanceId);
        Assert.Equal(900, candidates[0].PreviewArguments.SelectToken("vehicle.instanceId")?.Value<int>());
        Assert.Equal(900, candidates[0].PreviewArguments["vehicleInstanceId"]?.Value<int>());
        Assert.Equal(1d, candidates[0].TrainPowerScore);
        Assert.Equal(71, candidates[1].RailInternalId);
        Assert.Equal(0d, candidates[1].TrainPowerScore);
    }

    [Fact]
    public void ScorePreview_UsesObservedCycleAndRejectsWrongRailSpeedOrPollutedState()
    {
        RailInsertionCandidate candidate = Candidate(railInternalId: 71, stationCount: 3, cycle: 30d);

        Assert.True(_planner.TryScorePreview(
            candidate,
            Preview(71, currentCycle: 6d, predictedCycle: 7d),
            out RailInsertionPreviewScore score));
        Assert.Equal(0.5d, score.BaselineTriggerRate, 6);
        Assert.Equal(4d / 7d, score.PredictedTriggerRate, 6);
        Assert.True(score.IsBeneficial);

        Assert.False(_planner.TryScorePreview(candidate, Preview(72, 6d, 7d), out _));
        Assert.False(_planner.TryScorePreview(candidate, Preview(71, 6d, 7d, requiresSpeedSource: true), out _));
        Assert.False(_planner.TryScorePreview(candidate, Preview(71, 6d, 7d, statePolluted: true), out _));
    }

    [Fact]
    public void SelectBest_OnlyReturnsPositiveHighestAbsoluteTriggerGain()
    {
        RailInsertionPreviewScore weak = Score(71, baseline: 0.5d, predicted: 0.55d);
        RailInsertionPreviewScore strong = Score(72, baseline: 1d, predicted: 1.2d);
        RailInsertionPreviewScore loss = Score(73, baseline: 2d, predicted: 1.9d);

        Assert.Same(strong, _planner.SelectBest(new[] { weak, loss, strong }));
        Assert.Null(_planner.SelectBest(new[] { loss }));
    }

    [Fact]
    public void VerifyInsertion_RequiresSameRailSetAndExactAddedStation()
    {
        RailInsertionPreviewScore selected = Score(71, baseline: 0.5d, predicted: 0.6d);
        selected.Candidate.RailInstanceId = 701;
        selected.Candidate.StationCount = 3;
        selected.Candidate.StationLinePointInstanceId = 301;
        JObject baseline = Result(new
        {
            rails = new[] { Rail(701, 71, 3, 6d, 801, new[] { 11, 12, 13 }) }
        });
        JObject current = Result(new
        {
            rails = new[] { Rail(701, 71, 4, 6.8d, 801, new[] { 11, 12, 13, 301 }) }
        });

        RailInsertionVerification verified = _planner.VerifyInsertion(baseline, current, selected);

        Assert.True(verified.Verified);
        Assert.True(verified.Beneficial);
        Assert.Equal(6.8d, verified.ObservedLoopCycleSeconds, 6);

        JObject wrongPoint = Result(new
        {
            rails = new[] { Rail(701, 71, 4, 6.8d, 801, new[] { 11, 12, 13, 999 }) }
        });
        Assert.False(_planner.VerifyInsertion(baseline, wrongPoint, selected).Verified);
    }

    [Fact]
    public void VerifyInsertion_LegalCommittedStructureWithoutExpectedBenefit_IsVerifiedForRetry()
    {
        RailInsertionPreviewScore selected = Score(71, baseline: 0.5d, predicted: 0.6d);
        selected.Candidate.RailInstanceId = 701;
        selected.Candidate.StationCount = 3;
        selected.Candidate.StationLinePointInstanceId = 301;
        JObject baseline = Result(new
        {
            rails = new[] { Rail(701, 71, 3, 6d, 801, new[] { 11, 12, 13 }) }
        });
        JObject current = Result(new
        {
            rails = new[] { Rail(701, 71, 4, 10d, 801, new[] { 11, 12, 13, 301 }) }
        });

        RailInsertionVerification verification = _planner.VerifyInsertion(baseline, current, selected);

        Assert.True(verification.Verified);
        Assert.False(verification.Beneficial);
        Assert.Contains("结构合法", verification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingSpecialMove_RequiresExactFreshIdentityAndShorterLegalSameRailResult()
    {
        JObject baseline = Result(new
        {
            rails = new[] { SpecialRail(cycle: 10d) }
        });
        JObject sourceCatapults = Result(new
        {
            catapults = new[] { SpecialStation(401, 501, 301, x: 10, y: 10) }
        });
        RailStationMoveCandidate candidate = Assert.Single(
            _planner.BuildExistingSpecialMoveCandidates(baseline, sourceCatapults));
        IReadOnlyList<AutoPlayerGrid> rankedMoves = DefenseStationGridRanker.RankExistingStationMove(
            new[] { new AutoPlayerGrid(5, 5), new AutoPlayerGrid(10, 12) },
            candidate);
        Assert.NotEmpty(rankedMoves);
        Assert.Equal(new AutoPlayerGrid(10, 12), rankedMoves[0]);
        Assert.Contains(new AutoPlayerGrid(5, 5), rankedMoves);

        JObject movable = Result(new
        {
            stations = new[]
            {
                new
                {
                    instanceId = 401,
                    gameObjectInstanceId = 501,
                    path = "Scene/Special",
                    canMove = true
                }
            },
            currentMoveInteraction = new
            {
                active = true,
                interactionInstanceId = 901,
                target = new { instanceId = 501, path = "Scene/Special" }
            }
        });
        Assert.True(_planner.IsFreshMovableSpecial(sourceCatapults, movable, candidate));
        Assert.True(_planner.IsOwnedMoveInteraction(movable, candidate, 901));
        Assert.False(_planner.IsOwnedMoveInteraction(movable, candidate, 902));

        JObject current = Result(new
        {
            rails = new[] { SpecialRail(cycle: 8d, movedPointId: 302, movedX: 5, movedY: 5, railLength: 32d) }
        });
        JObject movedCatapults = Result(new
        {
            catapults = new[] { SpecialStation(402, 502, 302, x: 5, y: 5) }
        });
        RailInsertionVerification verification = _planner.VerifyMove(
            baseline,
            current,
            movedCatapults,
            candidate,
            JObject.FromObject(new { x = 5, y = 5 }));

        Assert.True(verification.Verified);

        JObject wrongRail = Result(new
        {
            catapults = new[] { SpecialStation(402, 502, 302, x: 5, y: 5, railId: 72) }
        });
        Assert.False(_planner.VerifyMove(
            baseline,
            current,
            wrongRail,
            candidate,
            JObject.FromObject(new { x = 5, y = 5 })).Verified);
    }

    [Fact]
    public void MoveCancellationRollback_RequiresOriginalIdentityGridAndRailShapeButAllowsSpeedCycleDrift()
    {
        JObject baseline = Result(new
        {
            rails = new[] { SpecialRail(cycle: 10d) }
        });
        JObject sourceCatapults = Result(new
        {
            catapults = new[] { SpecialStation(401, 501, 301, x: 10, y: 10) }
        });
        RailStationMoveCandidate candidate = Assert.Single(
            _planner.BuildExistingSpecialMoveCandidates(baseline, sourceCatapults));
        JObject restoredAfterWaveTransition = Result(new
        {
            catapults = new[] { SpecialStation(401, 501, 301, x: 10, y: 10, canMove: false) }
        });

        RailInsertionVerification verified = _planner.VerifyMoveCancellationRollback(
            baseline,
            baseline,
            restoredAfterWaveTransition,
            candidate);

        Assert.True(verified.Verified);
        Assert.Equal(10d, verified.ObservedLoopCycleSeconds, 6);

        JObject changedGrid = Result(new
        {
            catapults = new[] { SpecialStation(401, 501, 301, x: 10, y: 9, canMove: false) }
        });
        JObject changedIdentity = Result(new
        {
            catapults = new[] { SpecialStation(401, 502, 301, x: 10, y: 10, canMove: false) }
        });
        JObject changedCycle = Result(new
        {
            rails = new[] { SpecialRail(cycle: 8d) }
        });
        JObject changedRailSet = Result(new
        {
            rails = new[]
            {
                SpecialRail(cycle: 10d),
                Rail(702, 72, 3, 9d, 901, new[] { 21, 22, 23 })
            }
        });

        Assert.False(_planner.VerifyMoveCancellationRollback(
            baseline,
            baseline,
            changedGrid,
            candidate).Verified);
        Assert.False(_planner.VerifyMoveCancellationRollback(
            baseline,
            baseline,
            changedIdentity,
            candidate).Verified);
        Assert.True(_planner.VerifyMoveCancellationRollback(
            baseline,
            changedCycle,
            restoredAfterWaveTransition,
            candidate).Verified);
        Assert.False(_planner.VerifyMoveCancellationRollback(
            baseline,
            changedRailSet,
            restoredAfterWaveTransition,
            candidate).Verified);
    }

    [Fact]
    public void ExistingEnergyStation_UsesSameFreshMoveVerifyAndRollbackContract()
    {
        JObject baseline = Result(new { rails = new[] { SpecialRail(cycle: 10d) } });
        JObject sourceCatapults = Result(new
        {
            catapults = new[] { EnergyStation(401, 501, 301, x: 10, y: 10) }
        });
        RailStationMoveCandidate candidate = Assert.Single(
            _planner.BuildExistingSpecialMoveCandidates(baseline, sourceCatapults));
        Assert.True(candidate.StationIsAttribute);

        JObject movable = Result(new
        {
            stations = new[]
            {
                new
                {
                    instanceId = 401,
                    gameObjectInstanceId = 501,
                    path = "Scene/Energy",
                    canMove = true
                }
            }
        });
        Assert.True(_planner.IsFreshMovableSpecial(sourceCatapults, movable, candidate));

        JObject movedRail = Result(new
        {
            rails = new[] { SpecialRail(cycle: 8d, movedPointId: 302, movedX: 5, movedY: 5, railLength: 32d) }
        });
        JObject movedCatapults = Result(new
        {
            catapults = new[] { EnergyStation(402, 502, 302, x: 5, y: 5) }
        });
        Assert.True(_planner.VerifyMove(
            baseline,
            movedRail,
            movedCatapults,
            candidate,
            JObject.FromObject(new { x = 5, y = 5 })).Verified);
        Assert.True(_planner.VerifyMoveCancellationRollback(
            baseline,
            baseline,
            sourceCatapults,
            candidate).Verified);
    }

    [Fact]
    public void FreePointRanking_ExcludesCollinearTargetsAndRequiredDisposableRepairsCollinearLoop()
    {
        JObject catapults = Result(new
        {
            catapults = new object[]
            {
                AvailablePoint(100, isAttribute: true, 0, 0, "FreePoint_Attribute"),
                AvailablePoint(101, isAttribute: false, 1, 0, "FreePoint"),
                AvailablePoint(102, isAttribute: false, 2, 0, "FreePoint")
            }
        });

        IReadOnlyList<AutoPlayerGrid> ranked = DefenseStationGridRanker.RankPlacement(
            "FreePoint",
            new[] { new AutoPlayerGrid(3, 0), new AutoPlayerGrid(2, 1) },
            catapults);

        Assert.Equal("FreePoint", new BattleDecisionEngine().RequiredExpansionDisposable(catapults));
        Assert.Equal(new AutoPlayerGrid(2, 1), Assert.Single(ranked));
    }

    [Fact]
    public void StructuralGuard_ArmsAndAdvancesEachWriteStageOnlyOnce()
    {
        PendingDefenseMutationGuard guard = new();
        AutomationAction start = Action("startStationMove");
        AutomationAction confirm = Action("confirmStationMoveGrid");

        Assert.False(guard.TryArm(Action(string.Empty), "bad", 0f));
        Assert.False(guard.IsArmed);
        Assert.True(guard.TryArm(start, "start:401", 1f));
        Assert.True(guard.IsPreparedFor(start, "start:401"));
        Assert.False(guard.TryAdvance(confirm, "confirm:901", 1.1f));
        guard.MarkInvocationIssued();
        Assert.False(guard.IsPreparedFor(start, "start:401"));
        Assert.True(guard.TryAdvance(confirm, "confirm:901", 1.2f));
        Assert.True(guard.IsPreparedFor(confirm, "confirm:901"));
        guard.MarkInvocationIssued();
        Assert.False(guard.IsPreparedFor(confirm, "confirm:901"));
    }

    private static JObject Result(object state) => JObject.FromObject(new
    {
        success = true,
        data = new { state }
    });

    private static object Rail(
        int instanceId,
        int railInternalId,
        int stationCount,
        double cycle,
        int lineId,
        int[] pointIds) => new
    {
        instanceId,
        railInternalId,
        id = railInternalId,
        isLegalPlayerLoop = true,
        isLoop = true,
        isOnField = true,
        loopCycleSecondsKnown = true,
        loopCycleSeconds = cycle,
        stationCount,
        railLength = cycle * 2d,
        orderedStations = pointIds.Select(id => new { linePointInstanceId = id }),
        lines = new[] { new { lineInstanceId = lineId } }
    };

    private static object SpecialRail(
        double cycle,
        int movedPointId = 301,
        int movedX = 10,
        int movedY = 10,
        double railLength = 40d) => new
    {
        instanceId = 701,
        railInternalId = 71,
        id = 71,
        isLegalPlayerLoop = true,
        isLoop = true,
        isOnField = true,
        loopCycleSecondsKnown = true,
        loopCycleSeconds = cycle,
        stationCount = 3,
        railLength,
        orderedStations = new[]
        {
            new { linePointInstanceId = 11, grid = new { x = 0, y = 0 } },
            new { linePointInstanceId = movedPointId, grid = new { x = movedX, y = movedY } },
            new { linePointInstanceId = 13, grid = new { x = 20, y = 0 } }
        },
        lines = new object[]
        {
            new { lineInstanceId = 801, from = new { x = 0, y = 0 }, to = new { x = movedX, y = movedY } },
            new { lineInstanceId = 802, from = new { x = movedX, y = movedY }, to = new { x = 20, y = 0 } },
            new { lineInstanceId = 803, from = new { x = 20, y = 0 }, to = new { x = 0, y = 0 } }
        }
    };

    private static object UnusedStation(int pointId, int catapultId, int gameObjectId) => new
    {
        active = true,
        canUseForNewRail = true,
        canPickLine = true,
        frozen = false,
        railReachMax = false,
        isAttribute = false,
        railMembershipCount = 0,
        linePointInstanceId = pointId,
        catapultInstanceId = catapultId,
        gameObjectInstanceId = gameObjectId,
        path = "Scene/Unused",
        name = "普通弹射点",
        recycleDisposableEnum = "FreePoint",
        grid = new { x = 5, y = 5 }
    };

    private static object Train(int railId, int index, params object[] vehicles) => new
    {
        railId,
        index,
        vehicles
    };

    private static object Vehicle(int instanceId, int index, bool isTrainHead = false) => new
    {
        instanceId,
        index,
        isTrainHead,
        isFixedHead = isTrainHead
    };

    private static RailInsertionCandidate Candidate(int railInternalId, int stationCount, double cycle) => new()
    {
        RailInstanceId = 701,
        RailInternalId = railInternalId,
        LineInstanceId = 801,
        StationLinePointInstanceId = 301,
        StationCount = stationCount,
        CurrentLoopCycleSeconds = cycle
    };

    private static JObject Preview(
        int railInternalId,
        double currentCycle,
        double predictedCycle,
        bool requiresSpeedSource = false,
        bool statePolluted = false) => Result(new
    {
        wouldBeLegal = true,
        sideEffectCheckPassed = true,
        statePolluted,
        requiresSpeedSource,
        beforeRailCount = 2,
        afterRailCount = 2,
        affectedRailId = railInternalId,
        currentLoopCycleSeconds = currentCycle,
        predictedLoopCycleSeconds = predictedCycle
    });

    private static RailInsertionPreviewScore Score(int railInternalId, double baseline, double predicted)
    {
        RailLayoutPoint[] ring =
        {
            new(0, 2),
            new(2, 0),
            new(0, -2),
            new(-2, 0)
        };
        return new RailInsertionPreviewScore
        {
            Candidate = Candidate(railInternalId, 3, 6d),
            BaselineTriggerRate = baseline,
            PredictedTriggerRate = predicted,
            TriggerRateGain = predicted - baseline,
            RelativeGain = predicted / baseline - 1d,
            PredictedLoopCycleSeconds = 7d,
            BaselineLayout = RailLayoutStrategyPlanner.Evaluate(ring, 1, 1d / baseline),
            PredictedLayout = RailLayoutStrategyPlanner.Evaluate(ring, 1, 1d / predicted)
        };
    }

    private static object SpecialStation(
        int catapultId,
        int gameObjectId,
        int linePointId,
        int x,
        int y,
        int railId = 71,
        bool canMove = true) => new
    {
        instanceId = catapultId,
        catapultInstanceId = catapultId,
        gameObjectInstanceId = gameObjectId,
        linePointInstanceId = linePointId,
        name = "毒雾弹射点",
        path = "Scene/Special",
        active = true,
        canMove,
        canPickLine = true,
        isAttribute = false,
        isSpecial = true,
        railMembershipCount = 1,
        railId,
        recycleDisposableEnum = "PoisonStation",
        specialSource = "recycleDisposableEnum",
        effectEnum = "毒",
        pointBuffFlags = new[] { "Poison" },
        runtimeBuffIdentities = new[] { "Poison:2" },
        effectTags = new[] { "Poison" },
        grid = new { x, y }
    };

    private static object EnergyStation(
        int catapultId,
        int gameObjectId,
        int linePointId,
        int x,
        int y,
        int railId = 71,
        bool canMove = true) => new
    {
        instanceId = catapultId,
        catapultInstanceId = catapultId,
        gameObjectInstanceId = gameObjectId,
        linePointInstanceId = linePointId,
        name = "EnergyStation",
        path = "Scene/Energy",
        active = true,
        canMove,
        canPickLine = true,
        isAttribute = true,
        isSpecial = false,
        railMembershipCount = 1,
        railId,
        recycleDisposableEnum = "FreePoint_Attribute",
        specialSource = string.Empty,
        effectEnum = string.Empty,
        pointBuffFlags = Array.Empty<string>(),
        runtimeBuffIdentities = Array.Empty<string>(),
        effectTags = Array.Empty<string>(),
        grid = new { x, y }
    };

    private static object AvailablePoint(
        int pointId,
        bool isAttribute,
        int x,
        int y,
        string disposableEnum) => new
    {
        active = true,
        canUseForNewRail = true,
        canPickLine = true,
        frozen = false,
        railReachMax = false,
        railMembershipCount = 0,
        linePointInstanceId = pointId,
        isAttribute,
        recycleDisposableEnum = disposableEnum,
        grid = new { x, y }
    };

    private static AutomationAction Action(string command) => new(
        command,
        new JObject(),
        AutomationStage.PreparingDefense,
        "test");
}
