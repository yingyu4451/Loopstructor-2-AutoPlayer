using BepInEx.Configuration;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class PluginSettings
{
    public PluginSettings(ConfigFile config)
    {
        TickIntervalSeconds = config.Bind("Runtime", "TickIntervalSeconds", 0.75f, "Seconds between automation decisions.");
        MaxConsecutiveFailures = config.Bind("Runtime", "MaxConsecutiveFailures", 8, "Stop after this many consecutive command failures.");
        StallTimeoutSeconds = config.Bind("Runtime", "StallTimeoutSeconds", 90f, "Stop if no observable progress occurs for this duration.");
    }

    public ConfigEntry<float> TickIntervalSeconds { get; }
    public ConfigEntry<int> MaxConsecutiveFailures { get; }
    public ConfigEntry<float> StallTimeoutSeconds { get; }
}
