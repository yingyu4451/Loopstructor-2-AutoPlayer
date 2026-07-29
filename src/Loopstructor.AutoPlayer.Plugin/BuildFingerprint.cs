using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class BuildFingerprint
{
    private static readonly string[] ManagedAssemblyNames =
    {
        "Assembly-CSharp.dll",
        "ActFramework.Main.dll",
        "ActFramework.MainCore.dll",
        "ActFramework.RuntimeModule.Achievement.dll",
        "ActFramework.BuiltInTools.SteamworksNET.dll"
    };

    public string ProductName { get; private set; } = string.Empty;
    public string CompanyName { get; private set; } = string.Empty;
    public string ProductVersion { get; private set; } = string.Empty;
    public string UnityVersion { get; private set; } = string.Empty;
    public string BuildGuid { get; private set; } = string.Empty;
    public string SteamBuildId { get; private set; } = string.Empty;
    public string AssemblySha256 { get; private set; } = string.Empty;
    public string AssemblyMvid { get; private set; } = string.Empty;
    public IReadOnlyDictionary<string, string> ManagedAssemblySha256 { get; private set; } =
        new Dictionary<string, string>();

    public bool ProductIdentityValid =>
        string.Equals(CompanyName, "PoneGames", StringComparison.OrdinalIgnoreCase) &&
        ProductName.IndexOf("Skyspine", StringComparison.OrdinalIgnoreCase) >= 0;

    public bool MatchesExpectedAssembly(string expectedSha256) =>
        !string.IsNullOrWhiteSpace(expectedSha256) &&
        string.Equals(AssemblySha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    public static BuildFingerprint Capture()
    {
        BuildFingerprint fingerprint = new()
        {
            ProductName = Application.productName ?? string.Empty,
            CompanyName = Application.companyName ?? string.Empty,
            ProductVersion = Application.version ?? string.Empty,
            UnityVersion = Application.unityVersion ?? string.Empty,
            BuildGuid = Application.buildGUID ?? string.Empty,
            SteamBuildId = TryGetSteamBuildId(),
            AssemblyMvid = TryGetAssemblyMvid()
        };

        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        string managedRoot = Path.Combine(Application.dataPath, "Managed");
        foreach (string fileName in ManagedAssemblyNames)
        {
            string path = Path.Combine(managedRoot, fileName);
            string hash = TryHash(path);
            if (!string.IsNullOrEmpty(hash)) hashes[fileName] = hash;
        }

        fingerprint.ManagedAssemblySha256 = hashes;
        hashes.TryGetValue("Assembly-CSharp.dll", out string? assemblyHash);
        fingerprint.AssemblySha256 = assemblyHash ?? string.Empty;
        return fingerprint;
    }

    private static string TryHash(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetAssemblyMvid()
    {
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                {
                    return assembly.ManifestModule.ModuleVersionId.ToString("D");
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string TryGetSteamBuildId()
    {
        try
        {
            Type? steamApps = Type.GetType("Steamworks.SteamApps, com.rlabrecque.steamworks.net", false)
                              ?? Type.GetType("Steamworks.SteamApps, Assembly-CSharp", false);
            MethodInfo? method = steamApps?.GetMethod("GetAppBuildId", BindingFlags.Public | BindingFlags.Static);
            object? value = method?.Invoke(null, null);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
