using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class PlatformWriteIsolationPatch
{
    private const int RequiredPatchCount = 4;

    public static bool Applied { get; private set; }
    public static IReadOnlyList<string> PatchedEntries { get; private set; } = Array.Empty<string>();

    public static void Install(Harmony harmony, Action<string> log)
    {
        List<string> patched = new();
        TryPatch(harmony, "ActFramework_ByHZR.Achievements.Unit.SteamAchievementController", "UnlockAchievement", patched);
        TryPatch(harmony, "ActFramework_ByHZR.MainLoop.Version.PlatformAchievementBridge", "ReportIGPAchievement", patched);
        TryPatch(harmony, "MetroTD.UISystem.SettlementDataTotalManager", "TryAutoSendResultOnce", patched);
        TryPatchSteamRestart(harmony, patched);
        PatchedEntries = patched;
        Applied = patched.Count == RequiredPatchCount;
        log(Applied
            ? "Blocked external achievement and settlement uploads: " + string.Join(", ", patched)
            : $"External write isolation is incomplete ({patched.Count}/{RequiredPatchCount}): " + string.Join(", ", patched));
    }

    private static void TryPatch(Harmony harmony, string typeName, string methodName, ICollection<string> patched)
    {
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? original = type == null ? null : AccessTools.Method(type, methodName);
        MethodInfo? prefix = AccessTools.Method(typeof(PlatformWriteIsolationPatch), nameof(Skip));
        if (original == null || prefix == null)
        {
            return;
        }

        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        patched.Add(typeName + "." + methodName);
    }

    private static void TryPatchSteamRestart(Harmony harmony, ICollection<string> patched)
    {
        const string typeName = "Steamworks.SteamAPI";
        const string methodName = "RestartAppIfNecessary";
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? original = type == null ? null : AccessTools.Method(type, methodName);
        MethodInfo? prefix = AccessTools.Method(typeof(PlatformWriteIsolationPatch), nameof(SkipSteamRestart));
        if (original == null || prefix == null) return;
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        patched.Add(typeName + "." + methodName);
    }

    private static bool Skip() => false;

    private static bool SkipSteamRestart(ref bool __result)
    {
        __result = false;
        return false;
    }
}
