using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class SceneTransitionGateTests
{
    [Fact]
    public void KeepsWaitingWhileSourceSceneIsUnchanged()
    {
        SceneTransitionGate gate = new();
        DateTime started = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        gate.Begin("submitCommonMode", "StartGameScene", started);

        Assert.False(gate.ObserveScene("startgamescene"));
        Assert.True(gate.IsWaiting);
        Assert.Equal("submitCommonMode", gate.Command);
    }

    [Fact]
    public void ClearsOnlyAfterSceneActuallyChanges()
    {
        SceneTransitionGate gate = new();
        gate.Begin("submitCommonMode", "StartGameScene", DateTime.UtcNow);

        Assert.True(gate.ObserveScene("Temp"));
        Assert.False(gate.IsWaiting);
        Assert.Equal(string.Empty, gate.Command);
    }

    [Fact]
    public void TimesOutWithoutClearingTheCommand()
    {
        SceneTransitionGate gate = new();
        DateTime started = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        gate.Begin("continueGame", "StartGameScene", started);

        Assert.False(gate.HasTimedOut(started.AddSeconds(19), TimeSpan.FromSeconds(20)));
        Assert.True(gate.HasTimedOut(started.AddSeconds(20), TimeSpan.FromSeconds(20)));
        Assert.True(gate.IsWaiting);
    }

    [Fact]
    public void RejectsMissingCommandOrScene()
    {
        SceneTransitionGate gate = new();

        Assert.Throws<ArgumentException>(() => gate.Begin("", "StartGameScene", DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => gate.Begin("submitCommonMode", "", DateTime.UtcNow));
    }
}
