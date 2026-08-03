using System;
using System.Reflection;
using HarmonyLib;
using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class GameOutcomeObserver
{
    private static readonly object Sync = new();
    private static PropertyInfo? s_gameIsOverProperty;

    public static bool Installed { get; private set; }
    public static AutomationOutcome Outcome { get; private set; } = AutomationOutcome.Unknown;
    public static DateTime ObservedAtUtc { get; private set; }

    public static bool Install(Harmony harmony, Action<string> log)
    {
        Type? gameController = AccessTools.TypeByName("MetroTD.GameController");
        MethodInfo? gameWin = gameController == null
            ? null
            : AccessTools.Method(gameController, "GameWin", Type.EmptyTypes);
        MethodInfo? gameOver = gameController == null
            ? null
            : AccessTools.Method(gameController, "GameOver", Type.EmptyTypes);
        s_gameIsOverProperty = gameController == null
            ? null
            : AccessTools.Property(gameController, "GameIsOver");

        MethodInfo? prefix = AccessTools.Method(typeof(GameOutcomeObserver), nameof(ObserveBefore));
        MethodInfo? winPostfix = AccessTools.Method(typeof(GameOutcomeObserver), nameof(ObserveWinAfter));
        MethodInfo? lossPostfix = AccessTools.Method(typeof(GameOutcomeObserver), nameof(ObserveLossAfter));
        if (gameWin == null || gameOver == null || s_gameIsOverProperty == null ||
            prefix == null || winPostfix == null || lossPostfix == null)
        {
            log("无法安装胜负结果观察器：GameController 胜负契约不完整。");
            return false;
        }

        harmony.Patch(gameWin, new HarmonyMethod(prefix), new HarmonyMethod(winPostfix));
        harmony.Patch(gameOver, new HarmonyMethod(prefix), new HarmonyMethod(lossPostfix));
        Installed = true;
        log("已安装只读胜负结果观察器；不会改变游戏胜负流程。");
        return true;
    }

    public static void Reset()
    {
        lock (Sync)
        {
            Outcome = AutomationOutcome.Unknown;
            ObservedAtUtc = default;
        }
    }

    private static void ObserveBefore(object __instance, ref bool __state)
    {
        try
        {
            __state = s_gameIsOverProperty?.GetValue(__instance, null) is bool gameIsOver && !gameIsOver;
        }
        catch
        {
            __state = false;
        }
    }

    private static void ObserveWinAfter(bool __state)
    {
        if (__state)
        {
            Record(AutomationOutcome.Victory);
        }
    }

    private static void ObserveLossAfter(bool __state)
    {
        if (__state)
        {
            Record(AutomationOutcome.Defeat);
        }
    }

    private static void Record(AutomationOutcome outcome)
    {
        lock (Sync)
        {
            if (Outcome is AutomationOutcome.Victory or AutomationOutcome.Defeat)
            {
                return;
            }

            Outcome = outcome;
            ObservedAtUtc = DateTime.UtcNow;
        }
    }
}
