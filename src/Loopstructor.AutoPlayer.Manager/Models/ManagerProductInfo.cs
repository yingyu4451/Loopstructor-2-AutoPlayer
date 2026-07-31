using System.Reflection;

namespace Loopstructor.AutoPlayer.Manager.Models;

internal static class ManagerProductInfo
{
    public static string Version { get; } = ResolveVersion(typeof(ManagerProductInfo).Assembly);

    public static string DisplayText => FormatVersionLabel(Version);

    internal static string FormatVersionLabel(string? version)
    {
        string normalizedVersion = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();
        return $"AutoPlayer 版本 v{normalizedVersion}";
    }

    internal static string ResolveVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informationalVersion.Trim();
    }
}
