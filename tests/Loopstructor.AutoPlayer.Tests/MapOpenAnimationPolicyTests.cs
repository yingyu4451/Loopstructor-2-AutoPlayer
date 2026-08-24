using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class MapOpenAnimationPolicyTests
{
    [Fact]
    public void ActiveOpenAnimation_MustFinishEvenAfterFallbackDuration()
    {
        Assert.False(MapOpenAnimationPolicy.IsReady(
            animationReadable: true,
            openAnimationObservedNow: true,
            openAnimationObservedBefore: true,
            animatorReportsCompleted: false,
            elapsedSeconds: 2f,
            fallbackSeconds: 1.55f));
    }

    [Fact]
    public void CompletedOpenAnimation_IsReady()
    {
        Assert.True(MapOpenAnimationPolicy.IsReady(
            animationReadable: true,
            openAnimationObservedNow: true,
            openAnimationObservedBefore: true,
            animatorReportsCompleted: true,
            elapsedSeconds: 1.55f,
            fallbackSeconds: 1.55f));
    }

    [Fact]
    public void LeavingObservedOpenState_IsReady()
    {
        Assert.True(MapOpenAnimationPolicy.IsReady(
            animationReadable: true,
            openAnimationObservedNow: false,
            openAnimationObservedBefore: true,
            animatorReportsCompleted: false,
            elapsedSeconds: 1.6f,
            fallbackSeconds: 1.55f));
    }

    [Theory]
    [InlineData(false, 1.54f, false)]
    [InlineData(false, 1.55f, true)]
    [InlineData(true, 1.54f, false)]
    [InlineData(true, 1.55f, true)]
    public void MissingOpenState_UsesVerifiedAnimationDurationFallback(
        bool animationReadable,
        float elapsedSeconds,
        bool expected)
    {
        Assert.Equal(expected, MapOpenAnimationPolicy.IsReady(
            animationReadable,
            openAnimationObservedNow: false,
            openAnimationObservedBefore: false,
            animatorReportsCompleted: false,
            elapsedSeconds,
            fallbackSeconds: 1.55f));
    }
}
