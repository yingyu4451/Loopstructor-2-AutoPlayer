namespace Loopstructor.AutoPlayer.Core;

public static class MapRouteSelectionPolicy
{
    public static bool IsSelectionOutstanding(
        bool canSelectNextNode,
        bool canStartWave,
        bool hasChosenNode,
        bool hasPendingSublevelNode) =>
        canSelectNextNode &&
        !canStartWave &&
        !hasChosenNode &&
        !hasPendingSublevelNode;
}
