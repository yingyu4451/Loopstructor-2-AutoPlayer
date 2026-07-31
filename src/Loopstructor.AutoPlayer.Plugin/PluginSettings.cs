using BepInEx.Configuration;

namespace Loopstructor.AutoPlayer.Plugin;

internal sealed class PluginSettings
{
    public PluginSettings(ConfigFile config)
    {
        TickIntervalSeconds = config.Bind("Runtime", "TickIntervalSeconds", 0.75f, "自动游玩每次决策之间的秒数。");
        MaxConsecutiveFailures = config.Bind("Runtime", "MaxConsecutiveFailures", 8, "命令连续失败达到此次数后停止自动游玩。");
        StallTimeoutSeconds = config.Bind("Runtime", "StallTimeoutSeconds", 90f, "在此秒数内未观察到进展时停止自动游玩。");
    }

    public ConfigEntry<float> TickIntervalSeconds { get; }
    public ConfigEntry<int> MaxConsecutiveFailures { get; }
    public ConfigEntry<float> StallTimeoutSeconds { get; }
}
