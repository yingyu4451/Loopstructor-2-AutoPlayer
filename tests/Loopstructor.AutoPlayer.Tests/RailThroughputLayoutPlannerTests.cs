using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailThroughputLayoutPlannerTests
{
    [Fact]
    public void Evaluate_BaseEnclosingLoopBeatsOneSidedClusterAtComparableTriggerRate()
    {
        RailLayoutScore oneSided = RailLayoutStrategyPlanner.Evaluate(
            new[]
            {
                Point(-3, 1),
                Point(-4, 0),
                Point(-3, -1),
                Point(-2, 0)
            },
            stationCount: 4,
            loopCycleSeconds: 4d);
        RailLayoutScore enclosing = RailLayoutStrategyPlanner.Evaluate(
            new[]
            {
                Point(0, 2),
                Point(2, 0),
                Point(0, -2),
                Point(-2, 0)
            },
            stationCount: 4,
            loopCycleSeconds: 4d);

        Assert.False(oneSided.EncirclesBase);
        Assert.True(enclosing.EncirclesBase);
        Assert.True(enclosing.CoveredQuadrants > oneSided.CoveredQuadrants);
        Assert.True(RailLayoutStrategyPlanner.CompareForDefense(enclosing, oneSided) < 0);
    }

    [Fact]
    public void Evaluate_UsesStationHitsPerRealCycleAsThroughput()
    {
        RailLayoutPoint[] loop =
        {
            Point(0, 2),
            Point(2, 0),
            Point(0, -2),
            Point(-2, 0)
        };
        RailLayoutScore slow = RailLayoutStrategyPlanner.Evaluate(loop, stationCount: 4, loopCycleSeconds: 8d);
        RailLayoutScore fast = RailLayoutStrategyPlanner.Evaluate(loop, stationCount: 4, loopCycleSeconds: 4d);
        RailLayoutScore moreStations = RailLayoutStrategyPlanner.Evaluate(loop, stationCount: 8, loopCycleSeconds: 8d);

        Assert.Equal(0.5d, slow.TriggerRate, 6);
        Assert.Equal(1d, fast.TriggerRate, 6);
        Assert.Equal(fast.TriggerRate, moreStations.TriggerRate, 6);
        Assert.True(RailLayoutStrategyPlanner.CompareForDefense(fast, slow) < 0);
    }

    [Fact]
    public void CompareForDefense_WithRuntimeRules_ChoosesMinimumSpacingLayerBeforeCycle()
    {
        RailLayoutPoint[] compact = { Point(0, 2), Point(2, 0), Point(0, -2), Point(-2, 0) };
        RailLayoutPoint[] wide = { Point(0, 4), Point(4, 0), Point(0, -4), Point(-4, 0) };
        bool[] kinds = { true, false, false, false };
        StationSpacingRules rules = new(1.4d, 5d);
        RailLayoutScore compactSlow = RailLayoutStrategyPlanner.EvaluateWithSpacing(
            compact, kinds, 4, 8d, rules);
        RailLayoutScore wideFast = RailLayoutStrategyPlanner.EvaluateWithSpacing(
            wide, kinds, 4, 4d, rules);

        Assert.True(compactSlow.SpacingRulesKnown);
        Assert.True(compactSlow.AdjacentSpacingSurpluses[0] < wideFast.AdjacentSpacingSurpluses[0]);
        Assert.True(RailLayoutStrategyPlanner.CompareForDefense(compactSlow, wideFast) < 0);
    }

    [Fact]
    public void Evaluate_MaximumBlindArcIsHardUntilEveryDirectionIsCovered()
    {
        RailLayoutScore skewed = RailLayoutStrategyPlanner.Evaluate(
            new[] { Polar(4, 1), Polar(4, 91), Polar(4, 184), Polar(4, 359) },
            stationCount: 4,
            loopCycleSeconds: 4d);
        RailLayoutScore balanced = RailLayoutStrategyPlanner.Evaluate(
            new[] { Polar(4, 0), Polar(4, 90), Polar(4, 180), Polar(4, 270) },
            stationCount: 4,
            loopCycleSeconds: 4d);

        Assert.True(skewed.CoversAllQuadrants);
        Assert.False(skewed.HasNoLargeBlindArc);
        Assert.True(balanced.HasNoLargeBlindArc);
        Assert.True(RailLayoutStrategyPlanner.CompareCoverage(balanced, skewed) < 0);
    }

    [Fact]
    public void ExistingMovableEnergyStation_IsIncludedAndMoveCannotCollapseEnclosingLoop()
    {
        RailStationMoveCandidate candidate = new()
        {
            RailInstanceId = 701,
            RailInternalId = 71,
            StationCount = 4,
            CurrentLoopCycleSeconds = 8d,
            RailLength = 8d,
            CurrentGrid = new AutoPlayerGrid(0, 2),
            NeighborGrids = new[] { new AutoPlayerGrid(-2, 0), new AutoPlayerGrid(2, 0) },
            OrderedStationGrids = new[]
            {
                new AutoPlayerGrid(0, 2),
                new AutoPlayerGrid(0, -2),
                new AutoPlayerGrid(-2, 0),
                new AutoPlayerGrid(2, 0)
            },
            StationIsAttribute = true
        };
        RailExpansionPlanner planner = new();

        Assert.False(planner.IsBeneficialMove(candidate, new AutoPlayerGrid(0, 0)));
        Assert.True(planner.ScoreCurrentLayout(candidate).EncirclesBase);
        Assert.False(planner.ScoreMovedLayout(candidate, new AutoPlayerGrid(0, 0)).EncirclesBase);
    }

    [Fact]
    public void ExistingMovableStationMove_RequiresPositiveTriggerRateGain()
    {
        RailStationMoveCandidate candidate = new()
        {
            RailInstanceId = 701,
            RailInternalId = 71,
            StationCount = 4,
            CurrentLoopCycleSeconds = 10d,
            RailLength = 12d,
            CurrentGrid = new AutoPlayerGrid(4, 4),
            NeighborGrids = new[] { new AutoPlayerGrid(0, 4), new AutoPlayerGrid(4, 0) },
            OrderedStationGrids = new[]
            {
                new AutoPlayerGrid(4, 4),
                new AutoPlayerGrid(4, 0),
                new AutoPlayerGrid(0, 0),
                new AutoPlayerGrid(0, 4)
            }
        };
        RailExpansionPlanner planner = new();

        Assert.True(planner.IsBeneficialMove(candidate, new AutoPlayerGrid(3, 3)));
        Assert.False(planner.IsBeneficialMove(candidate, new AutoPlayerGrid(5, 5)));
    }

    [Fact]
    public void ExistingMovableStationMove_CanRepairDirectionalCoverageBeforeCycleGetsShorter()
    {
        RailStationMoveCandidate candidate = new()
        {
            RailInstanceId = 701,
            RailInternalId = 71,
            StationCount = 4,
            CurrentLoopCycleSeconds = 4d,
            RailLength = 12d,
            CurrentGrid = new AutoPlayerGrid(-4, 2),
            NeighborGrids = new[] { new AutoPlayerGrid(-2, 2), new AutoPlayerGrid(-4, -2) },
            OrderedStationGrids = new[]
            {
                new AutoPlayerGrid(-4, 2),
                new AutoPlayerGrid(-4, -2),
                new AutoPlayerGrid(-2, -2),
                new AutoPlayerGrid(-2, 2)
            }
        };
        RailExpansionPlanner planner = new();
        AutoPlayerGrid target = new(2, 2);

        Assert.True(planner.ScoreMovedLayout(candidate, target).LoopCycleSeconds >
                    planner.ScoreCurrentLayout(candidate).LoopCycleSeconds);
        Assert.True(RailLayoutStrategyPlanner.CompareCoverage(
            planner.ScoreMovedLayout(candidate, target),
            planner.ScoreCurrentLayout(candidate)) < 0);
        Assert.True(planner.IsBeneficialMove(candidate, target));

        IReadOnlyList<AutoPlayerGrid> ranked = DefenseStationGridRanker.RankExistingStationMove(
            new[] { new AutoPlayerGrid(-3, 1), target },
            candidate);
        Assert.Equal(target, ranked[0]);
    }

    [Fact]
    public void SelectBest_AcceptsCoverageRepairEvenWhenImmediateTriggerRateDrops()
    {
        RailLayoutScore oneSided = RailLayoutStrategyPlanner.Evaluate(
            new[] { Point(-4, 2), Point(-4, -2), Point(-2, -2), Point(-2, 2) },
            stationCount: 4,
            loopCycleSeconds: 4d);
        RailLayoutScore repaired = RailLayoutStrategyPlanner.Evaluate(
            new[] { Point(-4, 2), Point(-4, -2), Point(-2, -2), Point(-2, 2), Point(2, 0) },
            stationCount: 5,
            loopCycleSeconds: 6d);
        RailInsertionPreviewScore repair = new()
        {
            Candidate = new RailInsertionCandidate { RailInstanceId = 701 },
            BaselineTriggerRate = oneSided.TriggerRate,
            PredictedTriggerRate = repaired.TriggerRate,
            TriggerRateGain = repaired.TriggerRate - oneSided.TriggerRate,
            RelativeGain = repaired.TriggerRate / oneSided.TriggerRate - 1d,
            BaselineLayout = oneSided,
            PredictedLayout = repaired,
            PredictedLoopCycleSeconds = repaired.LoopCycleSeconds
        };

        Assert.True(RailLayoutStrategyPlanner.CompareCoverage(repaired, oneSided) < 0);
        Assert.False(repair.IsBeneficial);
        Assert.Same(repair, new RailExpansionPlanner().SelectBest(new[] { repair }));
    }

    [Fact]
    public void SelectBest_AcrossRailsUsesTrainPowerTimesStationHitsPerCycle()
    {
        RailLayoutPoint[] ring = { Point(0, 2), Point(2, 0), Point(0, -2), Point(-2, 0) };
        RailInsertionPreviewScore highRawRate = EffectiveScore(
            railInstanceId: 701,
            baseline: RailLayoutStrategyPlanner.Evaluate(ring, 4, 4d),
            predicted: RailLayoutStrategyPlanner.Evaluate(ring, 5, 4.5d),
            trainPower: 1d);
        RailInsertionPreviewScore highCombatOutput = EffectiveScore(
            railInstanceId: 702,
            baseline: RailLayoutStrategyPlanner.Evaluate(ring, 4, 5d),
            predicted: RailLayoutStrategyPlanner.Evaluate(ring, 5, 5.5d),
            trainPower: 9d);

        Assert.True(highRawRate.PredictedTriggerRate > highCombatOutput.PredictedTriggerRate);
        Assert.True(highCombatOutput.PredictedEffectiveAttackRate > highRawRate.PredictedEffectiveAttackRate);
        Assert.Same(
            highCombatOutput,
            new RailExpansionPlanner().SelectBest(new[] { highRawRate, highCombatOutput }));
    }

    [Fact]
    public void PlanPlayerLoop_LiveShapeBuildsCompactFourDirectionRingAndDropsRemoteOutlier()
    {
        RailLoopPointCandidate[] points =
        {
            Candidate(1, true, 6, 2),
            Candidate(2, false, -4, 4),
            Candidate(3, false, -4, -4),
            Candidate(4, false, -6, -2),
            Candidate(5, false, -6, 2),
            Candidate(6, false, -8, 0),
            Candidate(7, false, -2, -6),
            Candidate(8, false, 0, -8),
            Candidate(9, false, 2, -6),
            Candidate(10, false, 4, -4),
            Candidate(11, false, 6, -2),
            Candidate(12, false, 4, 4),
            Candidate(13, false, -2, 6),
            Candidate(14, false, 2, 6),
            Candidate(15, false, 0, 8),
            Candidate(16, false, 8, 0),
            Candidate(99, false, -16, -6)
        };

        RailLoopPlan plan = Assert.IsType<RailLoopPlan>(RailLayoutStrategyPlanner.PlanPlayerLoop(points));

        Assert.Equal(1, plan.OrderedPointInstanceIds[0]);
        Assert.DoesNotContain(99, plan.OrderedPointInstanceIds);
        Assert.True(plan.OrderedPointInstanceIds.Count >= 4);
        Assert.True(plan.Score.EncirclesBase);
        Assert.True(plan.Score.CoversAllQuadrants);
        Assert.True(plan.Score.HasNoLargeBlindArc);
    }

    private static RailLayoutPoint Point(double x, double y) => new(x, y);

    private static RailLayoutPoint Polar(double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return Point(radius * Math.Cos(radians), radius * Math.Sin(radians));
    }

    private static RailLoopPointCandidate Candidate(
        int instanceId,
        bool isAttribute,
        double x,
        double y) => new()
    {
        InstanceId = instanceId,
        IsAttribute = isAttribute,
        Grid = Point(x, y)
    };

    private static RailInsertionPreviewScore EffectiveScore(
        int railInstanceId,
        RailLayoutScore baseline,
        RailLayoutScore predicted,
        double trainPower) => new()
    {
        Candidate = new RailInsertionCandidate { RailInstanceId = railInstanceId },
        BaselineTriggerRate = baseline.TriggerRate,
        PredictedTriggerRate = predicted.TriggerRate,
        TriggerRateGain = predicted.TriggerRate - baseline.TriggerRate,
        RelativeGain = predicted.TriggerRate / baseline.TriggerRate - 1d,
        BaselineEffectiveAttackRate = trainPower * baseline.TriggerRate,
        PredictedEffectiveAttackRate = trainPower * predicted.TriggerRate,
        EffectiveAttackRateGain = trainPower * (predicted.TriggerRate - baseline.TriggerRate),
        PredictedLoopCycleSeconds = predicted.LoopCycleSeconds,
        BaselineLayout = baseline,
        PredictedLayout = predicted
    };
}
