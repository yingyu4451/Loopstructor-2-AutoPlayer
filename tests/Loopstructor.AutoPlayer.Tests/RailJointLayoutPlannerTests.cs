using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailJointLayoutPlannerTests
{
    [Fact]
    public void TwoMovableStations_ArePlannedTogetherIntoOneFinalFourDirectionLayout()
    {
        RailStationMoveCandidate[] stations = Candidates(
            new AutoPlayerGrid(-4, 1),
            new AutoPlayerGrid(-4, -1));
        AutoPlayerGrid[] ordinary =
        {
            new(-4, 1), new(-4, -1), new(-4, 4), new(4, -4), new(-4, -4)
        };
        AutoPlayerGrid[] energy = { new(4, 4), new(4, 0) };

        RailJointLayoutPlan? plan = RailJointLayoutSearch.FindBest(stations, ordinary, energy);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Targets.Count);
        Assert.Equal(new[] { 102, 103 }, plan.Targets.Select(target => target.StablePointId));
        Assert.Equal(4, plan.PredictedScore.CoveredQuadrants);
        Assert.True(plan.PredictedScore.EncirclesBase);
        Assert.True(RailLayoutStrategyPlanner.IsStrictDefenseImprovement(
            plan.BaselineScore,
            plan.PredictedScore));
        Assert.All(plan.Targets.GroupBy(target => target.StablePointId), group => Assert.Single(group));
    }

    [Fact]
    public void StablePointIdsRemainTheIdentityWhenUnityInstanceIdsChange()
    {
        RailStationMoveCandidate[] stations = Candidates(
            new AutoPlayerGrid(-4, 1),
            new AutoPlayerGrid(-4, -1));
        RailJointLayoutPlan plan = Assert.IsType<RailJointLayoutPlan>(
            RailJointLayoutSearch.FindBest(
                stations,
                new[] { new AutoPlayerGrid(-4, 4), new AutoPlayerGrid(4, -4), new AutoPlayerGrid(-4, -4) },
                new[] { new AutoPlayerGrid(4, 4) }));

        Assert.Equal(new[] { 102, 103 }, plan.Targets.Select(target => target.StablePointId));
        Assert.DoesNotContain(plan.Targets, target => target.StablePointId == target.Candidate.StationLinePointInstanceId);
    }

    [Fact]
    public void ThreeStations_UsesDeterministicBeamAndThreeMillisecondSlices()
    {
        RailStationMoveCandidate[] source = Candidates(
            new AutoPlayerGrid(-4, 1),
            new AutoPlayerGrid(-4, -1));
        RailStationMoveCandidate third = Clone(source[1], 104, 9004, new AutoPlayerGrid(1, -4), false);
        third.OrderedStationPointIds = new[] { 101, 102, 103, 104, 105 };
        third.OrderedStationGrids = new[]
        {
            new AutoPlayerGrid(4, 0), new AutoPlayerGrid(-4, 1), new AutoPlayerGrid(-4, -1),
            new AutoPlayerGrid(1, -4), new AutoPlayerGrid(0, 4)
        };
        third.OrderedStationKinds = new[] { true, false, false, false, false };
        foreach (RailStationMoveCandidate station in source)
        {
            station.OrderedStationPointIds = third.OrderedStationPointIds;
            station.OrderedStationGrids = third.OrderedStationGrids;
            station.OrderedStationKinds = third.OrderedStationKinds;
            station.StationCount = 5;
        }

        RailJointLayoutSearch search = new(
            source.Concat(new[] { third }),
            Enumerable.Range(-8, 17).SelectMany(x => Enumerable.Range(-8, 17).Select(y => new AutoPlayerGrid(x, y))),
            new[] { new AutoPlayerGrid(4, 0), new AutoPlayerGrid(4, 4) });
        RailJointLayoutSearchResult first = search.ProbeNext();

        Assert.Equal(RailJointLayoutSearchStatus.Probing, first.Status);
        Assert.InRange(first.SliceMilliseconds, 0d, 6d);
        Assert.Equal(512, RailJointLayoutSearch.BeamWidth);
        Assert.Equal(3d, RailJointLayoutSearch.DefaultSliceBudgetMilliseconds);
    }

    [Fact]
    public void ThreeStations_CompletedBeamLayoutsAreStillEvaluatedIncrementally()
    {
        RailStationMoveCandidate[] source = Candidates(
            new AutoPlayerGrid(-4, 1),
            new AutoPlayerGrid(-4, -1));
        RailStationMoveCandidate third = Clone(source[1], 104, 9004, new AutoPlayerGrid(1, -4), false);
        int[] ids = { 101, 102, 103, 104, 105 };
        AutoPlayerGrid[] grids =
        {
            new(4, 0), new(-4, 1), new(-4, -1), new(1, -4), new(0, 4)
        };
        bool[] kinds = { true, false, false, false, false };
        foreach (RailStationMoveCandidate station in source.Concat(new[] { third }))
        {
            station.OrderedStationPointIds = ids;
            station.OrderedStationGrids = grids;
            station.OrderedStationKinds = kinds;
            station.StationCount = grids.Length;
        }

        RailJointLayoutSearch search = new(
            source.Concat(new[] { third }),
            Enumerable.Range(-6, 13).SelectMany(x =>
                Enumerable.Range(-6, 13).Select(y => new AutoPlayerGrid(x, y))),
            new[] { new AutoPlayerGrid(4, 0), new AutoPlayerGrid(4, 4) });
        List<RailJointLayoutSearchResult> slices = new();
        RailJointLayoutSearchResult result;
        do
        {
            result = search.ProbeNext();
            slices.Add(result);
        } while (result.Status == RailJointLayoutSearchStatus.Probing && slices.Count < 5000);

        Assert.NotEqual(RailJointLayoutSearchStatus.Probing, result.Status);
        Assert.All(slices, slice => Assert.InRange(slice.SliceMilliseconds, 0d, 50d));
        Assert.InRange(slices.Average(slice => slice.SliceMilliseconds), 0d, 5d);
        Assert.True(slices.Count(slice => slice.EvaluatedLayoutCount > 0) > 1);
    }

    private static RailStationMoveCandidate[] Candidates(AutoPlayerGrid first, AutoPlayerGrid second)
    {
        int[] ids = { 101, 102, 103, 105 };
        AutoPlayerGrid[] grids = { new(4, 4), first, second, new(4, -4) };
        bool[] kinds = { true, false, false, false };
        return new[]
        {
            Candidate(102, 9002, first, false, ids, grids, kinds),
            Candidate(103, 9003, second, false, ids, grids, kinds)
        };
    }

    private static RailStationMoveCandidate Candidate(
        int stableId,
        int unityId,
        AutoPlayerGrid grid,
        bool attribute,
        int[] ids,
        AutoPlayerGrid[] grids,
        bool[] kinds) => new()
    {
        RailInstanceId = 700,
        RailInternalId = 70,
        StationCount = grids.Length,
        CurrentLoopCycleSeconds = 12d,
        RailLength = 24d,
        StationPointId = stableId,
        StationLinePointInstanceId = unityId,
        StationCatapultInstanceId = unityId + 100,
        StationGameObjectInstanceId = unityId + 200,
        StationPath = "Field/" + stableId,
        StationName = "特殊站点" + stableId,
        StationDisposableEnum = "Special" + stableId,
        StationFingerprint = "fp-" + stableId,
        StationIsAttribute = attribute,
        SpacingRules = new StationSpacingRules(2d, 3d),
        CurrentGrid = grid,
        OrderedStationPointIds = ids,
        OrderedStationGrids = grids,
        OrderedStationKinds = kinds
    };

    private static RailStationMoveCandidate Clone(
        RailStationMoveCandidate source,
        int stableId,
        int unityId,
        AutoPlayerGrid grid,
        bool attribute) => Candidate(
        stableId,
        unityId,
        grid,
        attribute,
        source.OrderedStationPointIds.ToArray(),
        source.OrderedStationGrids.ToArray(),
        source.OrderedStationKinds.ToArray());
}
