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
            VerificationError = "The save path hook contract is incomplete.";
            log(VerificationError);
            return false;
        }

        HashSet<MethodBase> targets = new() { wrapper, handler };
        foreach (MethodBase target in targets)
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        Installed = true;
        log("Save isolation hooks are installed for this activated process: " + s_profileRoot);
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
                    VerificationError = "The SaveManager verification contract is unavailable.";
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
                    VerificationError = "SaveManager resolved outside the isolated profile: " + fullPath;
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
                VerificationError = "Could not verify the isolated save folder: " + exception.Message;
            }
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
