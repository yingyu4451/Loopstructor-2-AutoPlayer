using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class SaveIsolationPatch
{
    private static readonly object Sync = new();
    private static string s_profileRoot = string.Empty;

    public static bool Installed { get; private set; }
    public static bool Applied { get; private set; }
    public static bool Verified { get; private set; }
    public static bool VerificationFailed { get; private set; }
    public static string VerificationError { get; private set; } = string.Empty;
    public static string IsolatedRoot => s_profileRoot;

    public static bool Install(Harmony harmony, string profileRoot, Action<string> log)
    {
        s_profileRoot = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(s_profileRoot);

        Type? type = AccessTools.TypeByName("ActFramework_ByHZR.Save.SavePathUtility");
        MethodInfo? wrapper = type == null
            ? null
            : AccessTools.Method(type, "GetCompanyAppDataPath", new[] { typeof(bool), typeof(string) });
        MethodInfo? handler = type == null
            ? null
            : AccessTools.Method(type, "GetCompanyAppDataPathHandle", new[] { typeof(bool), typeof(string) });
        MethodInfo? prefix = AccessTools.Method(typeof(SaveIsolationPatch), nameof(Prefix));
        if (wrapper == null || handler == null || prefix == null)
        {
            VerificationFailed = true;
            VerificationError = "存档路径挂钩契约不完整。";
            log(VerificationError);
            return false;
        }

        HashSet<MethodBase> targets = new() { wrapper, handler };
        foreach (MethodBase target in targets)
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        Installed = true;
        log("已为当前激活进程安装存档隔离挂钩：" + s_profileRoot);
        return true;
    }

    public static void ProbeRuntimeSaveFolder()
    {
        lock (Sync)
        {
            if (Verified || VerificationFailed || !Installed) return;

            try
            {
                Type? type = AccessTools.TypeByName("ActFramework_ByHZR.Save.SaveManager");
                MethodInfo? getter = type == null ? null : AccessTools.PropertyGetter(type, "Instance");
                MethodInfo? getPath = type == null ? null : AccessTools.Method(type, "GetSaveFolderPath", Type.EmptyTypes);
                if (getter == null || getPath == null)
                {
                    VerificationFailed = true;
                    VerificationError = "SaveManager 验证契约不可用。";
                    return;
                }

                object? instance = getter.Invoke(null, null);
                if (instance == null) return;
                string? actualPath = getPath.Invoke(instance, null) as string;
                if (string.IsNullOrWhiteSpace(actualPath)) return;

                string fullPath = Path.GetFullPath(actualPath);
                if (!IsInside(fullPath, s_profileRoot))
                {
                    VerificationFailed = true;
                    VerificationError = "SaveManager 解析到隔离存档之外的路径：" + fullPath;
                    return;
                }

                // The runtime path is the safety boundary. Mark the hook as applied
                // after the probe confirms the effective SaveManager destination.
                Applied = true;
                Verified = true;
                VerificationError = string.Empty;
            }
            catch (TargetInvocationException)
            {
                // SaveManager is not ready during the first scene's initialization.
            }
            catch (NullReferenceException)
            {
                // Framework globals are not ready yet.
            }
            catch (Exception exception)
            {
                VerificationFailed = true;
                VerificationError = "无法验证隔离存档目录：" + exception.Message;
            }
        }
    }

    public static bool TryResolveRuntimeSaveFolder(out string saveRoot)
    {
        saveRoot = string.Empty;
        try
        {
            Type? type = AccessTools.TypeByName("ActFramework_ByHZR.Save.SaveManager");
            MethodInfo? getter = type == null ? null : AccessTools.PropertyGetter(type, "Instance");
            MethodInfo? getPath = type == null ? null : AccessTools.Method(type, "GetSaveFolderPath", Type.EmptyTypes);
            object? instance = getter?.Invoke(null, null);
            string? path = instance == null ? null : getPath?.Invoke(instance, null) as string;
            if (string.IsNullOrWhiteSpace(path)) return false;

            string fullPath = Path.GetFullPath(path);
            if (!Path.IsPathRooted(fullPath) || !Directory.Exists(fullPath)) return false;
            saveRoot = fullPath;
            return true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (NullReferenceException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception)
        {
            // SaveManager may be between scene lifetimes. A passive path probe must never
            // make the plugin noisy or interrupt the game loop.
            return false;
        }
    }

    private static bool Prefix(ref string __result)
    {
        __result = s_profileRoot.Replace('\\', '/');
        Applied = true;
        return false;
    }

    private static bool IsInside(string candidate, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
