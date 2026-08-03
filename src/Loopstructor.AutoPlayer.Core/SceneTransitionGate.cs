using System;

namespace Loopstructor.AutoPlayer.Core;

public sealed class SceneTransitionGate
{
    public string Command { get; private set; } = string.Empty;
    public string SourceScene { get; private set; } = string.Empty;
    public DateTime StartedAtUtc { get; private set; }
    public bool IsWaiting => !string.IsNullOrEmpty(Command);

    public void Begin(string command, string sourceScene, DateTime startedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A transition command is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(sourceScene))
        {
            throw new ArgumentException("A source scene is required.", nameof(sourceScene));
        }

        Command = command;
        SourceScene = sourceScene;
        StartedAtUtc = startedAtUtc;
    }

    public bool ObserveScene(string scene)
    {
        if (!IsWaiting || string.Equals(scene, SourceScene, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Reset();
        return true;
    }

    public bool HasTimedOut(DateTime nowUtc, TimeSpan timeout) =>
        IsWaiting && nowUtc - StartedAtUtc >= timeout;

    public void Reset()
    {
        Command = string.Empty;
        SourceScene = string.Empty;
        StartedAtUtc = default;
    }
}
