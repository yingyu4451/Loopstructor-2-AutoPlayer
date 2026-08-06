namespace Loopstructor.AutoPlayer.Core;

public static class OpeningDefensePolicy
{
    public static bool ShouldPrepare(
        bool inWave,
        bool blocked,
        bool defensePrepared,
        bool pendingSublevel,
        bool mapOpen,
        bool canStartWave) =>
        !inWave &&
        !blocked &&
        !defensePrepared &&
        !pendingSublevel &&
        !mapOpen &&
        canStartWave;
}
