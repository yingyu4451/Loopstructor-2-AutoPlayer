using System;
using System.Reflection;
using HarmonyLib;

namespace Loopstructor.AutoPlayer.Plugin;

/// <summary>
/// Runs spawn-point capture inside the game's input sampling pipeline.
/// </summary>
internal static class SpawnPointCaptureInputPatch
{
    private static Action? _afterInputSampled;

    public static bool IsInstalled { get; private set; }
    public static bool IsDispatching { get; private set; }

    public static void Register(Action afterInputSampled) =>
        _afterInputSampled = afterInputSampled ?? throw new ArgumentNullException(nameof(afterInputSampled));

    public static bool Install(Harmony harmony, Action<string> log)
    {
        if (IsInstalled) return true;

        try
        {
            Type? handlerType = AccessTools.TypeByName("ActFramework_ByHZR.DefaultInputHandler");
            MethodInfo? update = handlerType == null
                ? null
                : AccessTools.Method(handlerType, "Update", Type.EmptyTypes);
            MethodInfo? postfix = AccessTools.Method(typeof(SpawnPointCaptureInputPatch), nameof(AfterInputSampled));
            if (update == null || postfix == null)
            {
                log("无法安装怪物生成位置捕获补丁：未找到游戏输入采样入口。");
                return false;
            }

            HarmonyMethod patch = new(postfix) { priority = Priority.First };
            harmony.Patch(update, postfix: patch);
            IsInstalled = true;
            log("怪物生成位置捕获已接入游戏输入采样流水线。");
            return true;
        }
        catch (Exception exception)
        {
            log("无法安装怪物生成位置捕获补丁：" + exception.Message);
            return false;
        }
    }

    public static void Detach()
    {
        _afterInputSampled = null;
        IsInstalled = false;
        IsDispatching = false;
    }

    // DefaultInputHandler.Update first refreshes the read-only key/mouse snapshots
    // and world mouse position. This postfix therefore runs before the framework
    // advances to UI/object/gameplay interaction modules in the same main-loop tick.
    private static void AfterInputSampled()
    {
        IsDispatching = true;
        try
        {
            _afterInputSampled?.Invoke();
        }
        finally
        {
            IsDispatching = false;
        }
    }
}
