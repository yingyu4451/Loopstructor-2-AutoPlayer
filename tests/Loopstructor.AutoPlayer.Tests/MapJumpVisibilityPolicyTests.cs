using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MapJumpVisibilityPolicyTests
{
    [Fact]
    public void EmptyPath_UsesLayerBeforeFirstNode()
    {
        Assert.Equal(-1, MapJumpVisibilityPolicy.ResolveCurrentLayer(null));
        Assert.True(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(0, -1));
    }

    [Fact]
    public void LatestVisitedNode_DeterminesHiddenHistoryBoundary()
    {
        int currentLayer = MapJumpVisibilityPolicy.ResolveCurrentLayer(new MapJumpCoordinate(1, 2));

        Assert.False(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(0, currentLayer));
        Assert.False(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(1, currentLayer));
        Assert.False(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(2, currentLayer));
        Assert.True(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(3, currentLayer));
        Assert.True(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(4, currentLayer));
    }

    [Fact]
    public void InvalidNegativeCandidateLayer_IsNeverExposed()
    {
        Assert.False(MapJumpVisibilityPolicy.ShouldExposeForFreeJump(-1, -1));
    }
}
