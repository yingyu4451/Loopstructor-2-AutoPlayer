using System;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class SpawnPointCaptureInputPatch
{
    private static Action? m_afterInputSampled;

    public static bool IsInstalled => true;
    public static bool IsDispatching { get; private set; }

    public static void Register(Action afterInputSampled) =>
        m_afterInputSampled = afterInputSampled ?? throw new ArgumentNullException(nameof(afterInputSampled));

    public static void Dispatch()
    {
        IsDispatching = true;
        try
        {
            m_afterInputSampled?.Invoke();
        }
        finally
        {
            IsDispatching = false;
        }
    }

    public static void Detach()
    {
        m_afterInputSampled = null;
        IsDispatching = false;
    }
}
