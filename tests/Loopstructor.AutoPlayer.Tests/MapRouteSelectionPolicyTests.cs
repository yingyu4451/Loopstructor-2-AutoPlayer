using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MapRouteSelectionPolicyTests
{
    [Fact]
    public void ReadyNodesWithoutACommittedChoice_RequireSelection()
    {
        Assert.True(MapRouteSelectionPolicy.IsSelectionOutstanding(
            canSelectNextNode: true,
            canStartWave: false,
            hasChosenNode: false,
            hasPendingSublevelNode: false));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, false)]
    public void MissingOrCommittedRoute_DoesNotRequireAnotherSelection(
        bool canSelectNextNode,
        bool canStartWave,
        bool hasChosenNode,
        bool hasPendingSublevelNode)
    {
        Assert.False(MapRouteSelectionPolicy.IsSelectionOutstanding(
            canSelectNextNode,
            canStartWave,
            hasChosenNode,
            hasPendingSublevelNode));
    }
}
