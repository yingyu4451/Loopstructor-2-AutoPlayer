using Loopstructor.AutoPlayer.Core;

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

    private static RailLoopNode Node(int id, bool attribute, double x, double y) => new()
    {
        Id = id,
        IsAttribute = attribute,
        Point = new RailLayoutPoint(x, y)
    };

    private static RailLoopEdge Edge(int from, int to) => new() { FromId = from, ToId = to };
}
