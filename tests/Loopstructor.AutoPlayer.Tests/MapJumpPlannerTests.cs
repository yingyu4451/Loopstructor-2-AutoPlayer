using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MapJumpPlannerTests
{
    [Fact]
    public void FirstLayerTarget_UsesEmptyPredecessorPath()
    {
        IReadOnlyList<IReadOnlyList<MapJumpNode>> layers = BuildConnectedMap();

        bool success = MapJumpPlanner.TryCreatePlan(
            layers,
            new MapJumpCoordinate(0, 0),
            stageStep: 3,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.True(success);
        Assert.Equal(MapJumpPlanFailure.None, failure);
        Assert.NotNull(plan);
        Assert.Equal(0, plan.TargetStage);
        Assert.Empty(plan.PredecessorPath);
    }

    [Fact]
    public void ConnectedTarget_UsesItsActualPreviousNode()
    {
        IReadOnlyList<IReadOnlyList<MapJumpNode>> layers = BuildConnectedMap();

        bool success = MapJumpPlanner.TryCreatePlan(
            layers,
            new MapJumpCoordinate(1, 2),
            stageStep: 3,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.True(success);
        Assert.Equal(MapJumpPlanFailure.None, failure);
        Assert.NotNull(plan);
        Assert.Equal(new MapJumpCoordinate(1, 1), Assert.Single(plan.PredecessorPath));
    }

    [Fact]
    public void StageStartTarget_UsesPreviousStageAsNativeStartSignal()
    {
        IReadOnlyList<IReadOnlyList<MapJumpNode>> layers = BuildConnectedMap();

        bool success = MapJumpPlanner.TryCreatePlan(
            layers,
            new MapJumpCoordinate(1, 3),
            stageStep: 3,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.True(success);
        Assert.Equal(MapJumpPlanFailure.None, failure);
        Assert.NotNull(plan);
        Assert.Equal(1, plan.TargetStage);
        Assert.Equal(new MapJumpCoordinate(0, 2), Assert.Single(plan.PredecessorPath));
    }

    [Theory]
    [InlineData(0, 0)] // 已通过节点
    [InlineData(1, 2)] // 当前节点
    [InlineData(0, 4)] // 未来节点
    public void Planner_DoesNotRestrictTargetsByPreviousProgress(int x, int y)
    {
        IReadOnlyList<IReadOnlyList<MapJumpNode>> layers = BuildConnectedMap();

        bool success = MapJumpPlanner.TryCreatePlan(
            layers,
            new MapJumpCoordinate(x, y),
            stageStep: 5,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.True(success);
        Assert.Equal(MapJumpPlanFailure.None, failure);
        Assert.NotNull(plan);
    }

    [Fact]
    public void DisconnectedTarget_IsRejectedFailClosed()
    {
        IReadOnlyList<IReadOnlyList<MapJumpNode>> layers = BuildConnectedMap();

        bool success = MapJumpPlanner.TryCreatePlan(
            layers,
            new MapJumpCoordinate(2, 2),
            stageStep: 3,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.False(success);
        Assert.Null(plan);
        Assert.Equal(MapJumpPlanFailure.ConnectedPredecessorNotFound, failure);
    }

    [Theory]
    [InlineData(-1, 0, 3, MapJumpPlanFailure.InvalidTargetCoordinate)]
    [InlineData(0, -1, 3, MapJumpPlanFailure.InvalidTargetCoordinate)]
    [InlineData(0, 99, 3, MapJumpPlanFailure.TargetLayerOutOfRange)]
    [InlineData(0, 1, 0, MapJumpPlanFailure.InvalidStageStep)]
    public void InvalidPlanInput_IsRejected(
        int x,
        int y,
        int stageStep,
        MapJumpPlanFailure expectedFailure)
    {
        bool success = MapJumpPlanner.TryCreatePlan(
            BuildConnectedMap(),
            new MapJumpCoordinate(x, y),
            stageStep,
            out MapJumpPlan? plan,
            out MapJumpPlanFailure failure);

        Assert.False(success);
        Assert.Null(plan);
        Assert.Equal(expectedFailure, failure);
    }

    private static IReadOnlyList<IReadOnlyList<MapJumpNode>> BuildConnectedMap() =>
        new IReadOnlyList<MapJumpNode>[]
        {
            new[]
            {
                Node(0, 0, (0, 1)),
                Node(1, 0, (1, 1))
            },
            new[]
            {
                Node(0, 1, (0, 2)),
                Node(1, 1, (1, 2))
            },
            new[]
            {
                Node(0, 2),
                Node(1, 2),
                Node(2, 2)
            },
            new[]
            {
                Node(0, 3, (0, 4)),
                Node(1, 3, (0, 4))
            },
            new[]
            {
                Node(0, 4)
            }
        };

    private static MapJumpNode Node(int x, int y, params (int X, int Y)[] next) =>
        new(
            new MapJumpCoordinate(x, y),
            next.Select(coordinate => new MapJumpCoordinate(coordinate.X, coordinate.Y)).ToArray());
}
