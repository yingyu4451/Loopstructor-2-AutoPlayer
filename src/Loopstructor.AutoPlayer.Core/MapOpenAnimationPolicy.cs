namespace Loopstructor.AutoPlayer.Core;

public static class MapOpenAnimationPolicy
{
    public static bool IsReady(
        bool animationReadable,
        bool openAnimationObservedNow,
        bool openAnimationObservedBefore,
        bool animatorReportsCompleted,
        float elapsedSeconds,
        float fallbackSeconds)
    {
        if (animatorReportsCompleted) return true;
        if (animationReadable && openAnimationObservedBefore && !openAnimationObservedNow) return true;

        return elapsedSeconds >= fallbackSeconds &&
               (!animationReadable || (!openAnimationObservedNow && !openAnimationObservedBefore));
    }
}
