using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class RailLoopValidatorTests
{
    [Fact]
    public void Validate_AcceptsSimpleDiamondAroundBase()
    {
        RailLoopValidationResult result = RailLoopValidator.ValidateOrdered(new[]
        {
            Node(1, true, 0, 3),
            Node(2, false, 3, 0),
            Node(3, false, 0, -3),
            Node(4, false, -3, 0)
        });

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.True(result.EncirclesBase);
        Assert.True(result.CoversAllQuadrants);
        Assert.True(result.HasNoLargeBlindArc);
    }

    [Fact]
    public void Validate_AcceptsZeroBasedTriangleThatEnclosesBase()
    {
        RailLoopValidationResult result = RailLoopValidator.ValidateOrdered(new[]
        {
            Node(1, false, -2, -2),
            Node(2, true, 3, 0),
            Node(0, false, -2, 2)
        });

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(new[] { 2, 1, 0 }, result.OrderedNodeIds);
        Assert.True(result.EncirclesBase);
        Assert.True(result.CoversAllQuadrants);
        Assert.True(result.HasNoLargeBlindArc);
    }

    [Fact]
    public void Validate_AcceptsConcaveSimpleLoopThatStillContainsBase()
    {
        RailLoopValidationResult result = RailLoopValidator.ValidateOrdered(new[]
        {
            Node(1, true, 0, 4),
            Node(2, false, 4, 1),
            Node(3, false, 1, 0),
            Node(4, false, 4, -3),
            Node(5, false, -4, -3),
            Node(6, false, -4, 2)
        });

        Assert.True(result.IsSimpleGeometry);
        Assert.True(result.EncirclesBase);
    }

    [Fact]
    public void Validate_RejectsBowTieEvenWhenConvexHullWouldContainBase()
    {
        RailLoopValidationResult result = RailLoopValidator.ValidateOrdered(new[]
        {
            Node(1, true, -3, 3),
            Node(2, false, 3, -3),
            Node(3, false, -3, -3),
            Node(4, false, 3, 3)
        });

        Assert.False(result.IsValid);
        Assert.False(result.IsSimpleGeometry);
        Assert.True(result.SelfIntersectionCount > 0);
    }

    [Fact]
    public void Validate_RejectsDuplicateEdgeAndDegreeMismatch()
    {
        RailLoopNode[] nodes =
        {
            Node(1, true, 0, 3), Node(2, false, 3, 0),
            Node(3, false, 0, -3), Node(4, false, -3, 0)
        };
        RailLoopEdge[] edges =
        {
            Edge(1, 2), Edge(2, 3), Edge(2, 3), Edge(4, 1)
        };

        RailLoopValidationResult result = RailLoopValidator.Validate(nodes, edges);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("重复线段"));
        Assert.Contains(result.Errors, error => error.Contains("恰好连接两条"));
    }

    [Fact]
    public void Validate_RejectsDisconnectedComponents()
    {
        RailLoopNode[] nodes =
        {
            Node(1, true, 0, 4), Node(2, false, 2, 2), Node(3, false, -2, 2),
            Node(4, false, 0, -4), Node(5, false, 2, -2), Node(6, false, -2, -2)
        };
        RailLoopEdge[] edges =
        {
            Edge(1, 2), Edge(2, 3), Edge(3, 1),
            Edge(4, 5), Edge(5, 6), Edge(6, 4)
        };

        RailLoopValidationResult result = RailLoopValidator.Validate(nodes, edges);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("单一连通分量"));
    }

    [Fact]
    public void Validate_RejectsSimpleLoopThatDoesNotContainBase()
    {
        RailLoopValidationResult result = RailLoopValidator.ValidateOrdered(new[]
        {
            Node(1, true, 3, 3), Node(2, false, 5, 3),
            Node(3, false, 5, 1), Node(4, false, 3, 1)
        });

        Assert.False(result.IsValid);
        Assert.True(result.IsSimpleGeometry);
        Assert.False(result.EncirclesBase);
    }

    [Fact]
    public void BalancedDefenseRing_RejectsNeedleTriangleSeenInLiveGame()
    {
        RailLayoutScore score = RailLayoutStrategyPlanner.EvaluateEstimated(new[]
        {
            new RailLayoutPoint(-4, -4),
            new RailLayoutPoint(-8, -6),
            new RailLayoutPoint(16, 14)
        });

        Assert.False(RailLayoutStrategyPlanner.IsBalancedDefenseRing(score));
        Assert.True(score.MinAngularGapDegrees < 45d || score.RadiusRatio > 2.5d);
    }

    [Fact]
    public void BalancedDefenseRing_AcceptsCompactTriangleAroundBase()
    {
        RailLayoutScore score = RailLayoutStrategyPlanner.EvaluateEstimated(new[]
        {
            new RailLayoutPoint(0, 4),
            new RailLayoutPoint(4, -2),
            new RailLayoutPoint(-4, -2)
        });

        Assert.True(RailLayoutStrategyPlanner.IsBalancedDefenseRing(score));
    }

    [Fact]
    public void RuntimeInspector_RejectsTopologicalLoopWithIrregularOuterShape()
    {
        JObject rail = new()
        {
            ["instanceId"] = 701,
            ["orderedStations"] = new JArray(
                RuntimeStation(1, true, -2, -2),
                RuntimeStation(2, false, -4, -3),
                RuntimeStation(3, false, 5, 4)),
            ["lines"] = new JArray(
                RuntimeLine(-2, -2, -4, -3),
                RuntimeLine(-4, -3, 5, 4),
                RuntimeLine(5, 4, -2, -2))
        };

        RailRuntimeValidation validation = RailRuntimeTopologyInspector.InspectRail(rail);

        Assert.True(validation.Loop.IsValid);
        Assert.False(validation.IsDefenseValid);
        Assert.False(RailLayoutStrategyPlanner.IsBalancedDefenseRing(validation.Layout));
    }

    private static RailLoopNode Node(int id, bool attribute, double x, double y) => new()
    {
        Id = id,
        IsAttribute = attribute,
        Point = new RailLayoutPoint(x, y)
    };

    private static RailLoopEdge Edge(int from, int to) => new() { FromId = from, ToId = to };

    private static JObject RuntimeStation(int id, bool attribute, int x, int y) => new()
    {
        ["pointId"] = id,
        ["linePointInstanceId"] = id,
        ["isAttribute"] = attribute,
        ["grid"] = new JObject { ["x"] = x, ["y"] = y }
    };

    private static JObject RuntimeLine(int fromX, int fromY, int toX, int toY) => new()
    {
        ["from"] = new JObject { ["x"] = fromX, ["y"] = fromY },
        ["to"] = new JObject { ["x"] = toX, ["y"] = toY }
    };
}
