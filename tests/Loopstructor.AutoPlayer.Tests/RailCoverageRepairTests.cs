using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailCoverageRepairTests
{
    [Fact]
    public void ExistingStationMove_AllowsCoverageRepairBeforeTriggerRateOptimization()
    {
        RailStationMoveCandidate candidate = new()
        {
            RailInstanceId = 701,
            RailInternalId = 71,
            StationCount = 4,
            CurrentLoopCycleSeconds = 8d,
            RailLength = 16d,
            CurrentGrid = new AutoPlayerGrid(-3, 3),
            NeighborGrids = new[]
            {
                new AutoPlayerGrid(-1, 1),
                new AutoPlayerGrid(-3, -3)
            },
            OrderedStationGrids = new[]
            {
                new AutoPlayerGrid(-3, 3),
                new AutoPlayerGrid(-3, -3),
                new AutoPlayerGrid(-1, -1),
                new AutoPlayerGrid(-1, 1)
            },
            StationIsAttribute = true
        };
        RailExpansionPlanner planner = new();
        AutoPlayerGrid enclosingTarget = new(5, 0);

        RailLayoutScore baseline = planner.ScoreCurrentLayout(candidate);
        RailLayoutScore repaired = planner.ScoreMovedLayout(candidate, enclosingTarget);

        Assert.False(baseline.EncirclesBase);
        Assert.True(repaired.EncirclesBase);
        Assert.True(repaired.TriggerRate < baseline.TriggerRate);
        Assert.True(planner.IsBeneficialMove(candidate, enclosingTarget));
    }

    [Fact]
    public void RailInsertion_AllowsCoverageRepairBeforeTriggerRateOptimization()
    {
        RailLayoutScore oneSided = RailLayoutStrategyPlanner.Evaluate(
            new[]
            {
                new RailLayoutPoint(-4, 0),
                new RailLayoutPoint(-3, 2),
                new RailLayoutPoint(-3, -2)
            },
            stationCount: 3,
            loopCycleSeconds: 6d);
        RailLayoutScore enclosing = RailLayoutStrategyPlanner.Evaluate(
            new[]
            {
                new RailLayoutPoint(-4, 0),
                new RailLayoutPoint(-3, 2),
                new RailLayoutPoint(4, 0),
                new RailLayoutPoint(-3, -2)
            },
            stationCount: 4,
            loopCycleSeconds: 10d);
        RailInsertionPreviewScore repair = new()
        {
            Candidate = new RailInsertionCandidate
            {
                RailInstanceId = 701,
                RailInternalId = 71,
                LineInstanceId = 801,
                StationLinePointInstanceId = 901,
                StationCount = 3,
                CurrentLoopCycleSeconds = 6d
            },
            BaselineTriggerRate = oneSided.TriggerRate,
            PredictedTriggerRate = enclosing.TriggerRate,
            TriggerRateGain = enclosing.TriggerRate - oneSided.TriggerRate,
            RelativeGain = enclosing.TriggerRate / oneSided.TriggerRate - 1d,
            PredictedLoopCycleSeconds = enclosing.LoopCycleSeconds,
            BaselineLayout = oneSided,
            PredictedLayout = enclosing
        };

        Assert.True(RailLayoutStrategyPlanner.CompareCoverage(enclosing, oneSided) < 0);
        Assert.True(repair.TriggerRateGain < 0d);
        Assert.Same(repair, new RailExpansionPlanner().SelectBest(new[] { repair }));
    }

    [Fact]
    public void MoveVerification_AcceptsMeasuredCoverageRepairEvenWhenCycleIsLonger()
    {
        RailStationMoveCandidate candidate = CoverageRepairCandidate();
        JObject baseline = RailResult(
            cycle: 8d,
            (301, -3, 3),
            (302, -3, -3),
            (303, -1, -1),
            (304, -1, 1));
        JObject repaired = RailResult(
            cycle: 9d,
            (305, 5, 0),
            (302, -3, -3),
            (303, -1, -1),
            (304, -1, 1));
        JObject movedCatapults = Result(new
        {
            catapults = new[] { EnergyStation(402, 502, 305, 5, 0) }
        });

        RailInsertionVerification verification = new RailExpansionPlanner().VerifyMove(
            baseline,
            repaired,
            movedCatapults,
            candidate,
            JObject.FromObject(new { x = 5, y = 0 }));

        Assert.True(verification.Verified, verification.Detail);
        Assert.Equal(9d, verification.ObservedLoopCycleSeconds, 6);
    }

    [Fact]
    public void MoveVerification_TreatsCrossFrameBaselineRailSnapshotAsPending()
    {
        RailStationMoveCandidate candidate = CoverageRepairCandidate();
        JObject baseline = RailResult(
            cycle: 8d,
            (301, -3, 3),
            (302, -3, -3),
            (303, -1, -1),
            (304, -1, 1));
        JObject movedCatapults = Result(new
        {
            catapults = new[] { EnergyStation(402, 502, 305, 5, 0) }
        });

        RailInsertionVerification verification = new RailExpansionPlanner().VerifyMove(
            baseline,
            baseline,
            movedCatapults,
            candidate,
            JObject.FromObject(new { x = 5, y = 0 }));

        Assert.False(verification.Verified);
        Assert.True(verification.Pending, verification.Detail);
    }

    [Fact]
    public void InitialLoop_RejectsDistantOutlierWhenCompactFourDirectionRingExists()
    {
        RailLoopPlan? plan = RailLayoutStrategyPlanner.PlanPlayerLoop(new[]
        {
            Candidate(1, isAttribute: true, 6, 2),
            Candidate(2, isAttribute: false, -4, 4),
            Candidate(3, isAttribute: false, -4, -4),
            Candidate(4, isAttribute: false, -6, -2),
            Candidate(5, isAttribute: false, -6, 2),
            Candidate(6, isAttribute: false, -8, 0),
            Candidate(7, isAttribute: false, -2, -6),
            Candidate(8, isAttribute: false, 0, -8),
            Candidate(9, isAttribute: false, 2, -6),
            Candidate(10, isAttribute: false, 4, -4),
            Candidate(11, isAttribute: false, 6, -2),
            Candidate(12, isAttribute: false, 4, 4),
            Candidate(13, isAttribute: false, -2, 6),
            Candidate(14, isAttribute: false, 2, 6),
            Candidate(15, isAttribute: false, 0, 8),
            Candidate(16, isAttribute: false, 8, 0),
            Candidate(99, isAttribute: false, -16, -6)
        });

        Assert.NotNull(plan);
        Assert.True(plan.Score.EncirclesBase);
        Assert.True(plan.Score.CoversAllQuadrants);
        Assert.DoesNotContain(99, plan.OrderedPointInstanceIds);
    }

    private static JObject RailResult(
        double cycle,
        params (int Id, int X, int Y)[] stations) => Result(new
        {
            rails = new[]
            {
                new
                {
                    instanceId = 701,
                    railInternalId = 71,
                    id = 71,
                    isLegalPlayerLoop = true,
                    isLoop = true,
                    isOnField = true,
                    loopCycleSecondsKnown = true,
                    loopCycleSeconds = cycle,
                    stationCount = stations.Length,
                    railLength = cycle * 2d,
                    orderedStations = stations.Select(station => new
                    {
                        linePointInstanceId = station.Id,
                        grid = new { x = station.X, y = station.Y }
                    })
                }
            }
        });

    private static RailStationMoveCandidate CoverageRepairCandidate() => new()
    {
        RailInstanceId = 701,
        RailInternalId = 71,
        StationCount = 4,
        CurrentLoopCycleSeconds = 8d,
        RailLength = 16d,
        StationCatapultInstanceId = 401,
        StationGameObjectInstanceId = 501,
        StationLinePointInstanceId = 301,
        StationPath = "Scene/Energy",
        StationName = "EnergyStation",
        StationDisposableEnum = "FreePoint_Attribute",
        StationFingerprint = "EnergyStation|FreePoint_Attribute|||||",
        StationIsAttribute = true,
        CurrentGrid = new AutoPlayerGrid(-3, 3),
        NeighborGrids = new[]
        {
            new AutoPlayerGrid(-1, 1),
            new AutoPlayerGrid(-3, -3)
        },
        OrderedStationGrids = new[]
        {
            new AutoPlayerGrid(-3, 3),
            new AutoPlayerGrid(-3, -3),
            new AutoPlayerGrid(-1, -1),
            new AutoPlayerGrid(-1, 1)
        }
    };

    private static JObject Result(object state) => JObject.FromObject(new { state });

    private static object EnergyStation(
        int catapultId,
        int gameObjectId,
        int linePointId,
        int x,
        int y) => new
    {
        instanceId = catapultId,
        catapultInstanceId = catapultId,
        gameObjectInstanceId = gameObjectId,
        linePointInstanceId = linePointId,
        name = "EnergyStation",
        path = "Scene/Energy",
        active = true,
        canMove = true,
        canPickLine = true,
        isAttribute = true,
        isSpecial = false,
        railMembershipCount = 1,
        railId = 71,
        recycleDisposableEnum = "FreePoint_Attribute",
        specialSource = string.Empty,
        effectEnum = string.Empty,
        pointBuffFlags = Array.Empty<string>(),
        runtimeBuffIdentities = Array.Empty<string>(),
        effectTags = Array.Empty<string>(),
        grid = new { x, y }
    };

    private static RailLoopPointCandidate Candidate(
        int instanceId,
        bool isAttribute,
        int x,
        int y) => new()
    {
        InstanceId = instanceId,
        IsAttribute = isAttribute,
        Grid = new RailLayoutPoint(x, y)
    };
}
