using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Loopstructor.AutoPlayer.Plugin;

internal static class GameArtifactIsolationPatch
{
    private const int RequiredPatchCount = 4;
    private static string s_artifactRoot = string.Empty;
    private static Action<string>? s_log;

    public static bool Applied { get; private set; }

    public static void Install(Harmony harmony, string artifactRoot, Action<string> log)
    {
        s_artifactRoot = Path.GetFullPath(artifactRoot);
        s_log = log;
        Directory.CreateDirectory(s_artifactRoot);
        int patched = 0;
        patched += PatchPrefix(harmony,
            "MetroTD.DebugLoggerSystem.Handler.DefaultDebugLoggerHandler",
            "Init",
            nameof(RedirectDebugLoggerPaths));
        patched += PatchPrefix(harmony,
            "MetroTD.DebugLoggerSystem.LocalizationStartupDebugLogger",
            "GetDefaultLogPath",
            nameof(ReturnLogFile));
        patched += PatchPrefix(harmony,
            "MetroTD.UISystem.SettlementDataSaveManager",
            "GetFolderPath",
            nameof(ReturnSettlementDirectory));
        patched += PatchPrefix(harmony,
            "ActFramework_ByHZR.Pool.ObjectPoolHandler",
            "AutoSave",
            nameof(RedirectObjectPoolAutoSave));
        Applied = patched == RequiredPatchCount;
        log(Applied
            ? "Game diagnostic artifacts are redirected to: " + s_artifactRoot
            : $"Game artifact redirection is incomplete ({patched}/{RequiredPatchCount}).");
    }

    private static int PatchPrefix(Harmony harmony, string typeName, string methodName, string prefixName)
    {
        Type? type = AccessTools.TypeByName(typeName);
        MethodInfo? original = type == null ? null : AccessTools.Method(type, methodName);
        MethodInfo? prefix = AccessTools.Method(typeof(GameArtifactIsolationPatch), prefixName);
        if (original == null || prefix == null) return 0;
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        return 1;
    }

    private static void RedirectDebugLoggerPaths(object __instance)
    {
        try
        {
            FieldInfo? field = AccessTools.Field(__instance.GetType(), "loggers");
            if (field?.GetValue(__instance) is not IEnumerable loggers) return;
            string directory = Path.Combine(s_artifactRoot, "GameDebugLogs");
            Directory.CreateDirectory(directory);
            foreach (object logger in loggers)
            {
                if (logger == null) continue;
                PropertyInfo? tagProperty = AccessTools.Property(logger.GetType(), "LogTagAndFileName");
                PropertyInfo? pathProperty = AccessTools.Property(logger.GetType(), "LogFilePath");
                string tag = Convert.ToString(tagProperty?.GetValue(logger, null)) ?? "Debug";
                pathProperty?.SetValue(logger, Path.Combine(directory, Sanitize(tag) + ".log"), null);
            }
        }
        catch (Exception exception)
        {
            s_log?.Invoke("Could not redirect a game debug logger: " + exception.Message);
        }
    }

    private static bool ReturnLogFile(ref string __result)
    {
        string directory = Path.Combine(s_artifactRoot, "GameDebugLogs");
        Directory.CreateDirectory(directory);
        __result = Path.Combine(directory, "LocalizationStartup.log");
        return false;
    }

    private static bool ReturnSettlementDirectory(ref string __result)
    {
        __result = Path.Combine(s_artifactRoot, "GameSettlementData");
        Directory.CreateDirectory(__result);
        return false;
    }

    private static bool RedirectObjectPoolAutoSave(object __instance, string fn)
    {
        try
        {
            MethodInfo? buildCsv = AccessTools.Method(__instance.GetType(), "TryBuildPoolUsageCsv");
            object?[] arguments = { null };
            if (buildCsv?.Invoke(__instance, arguments) is not bool success || !success || arguments[0] is not string csv)
            {
                return false;
            }

            string directory = Path.Combine(s_artifactRoot, "ObjectUseData");
            Directory.CreateDirectory(directory);
            string fileName = Sanitize(fn) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
            File.WriteAllText(Path.Combine(directory, fileName), csv);
        }
        catch (Exception exception)
        {
            s_log?.Invoke("Could not redirect object-pool diagnostics: " + exception.Message);
        }

        return false;
    }

    private static string Sanitize(string? value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Debug" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        return result;
    }
}
