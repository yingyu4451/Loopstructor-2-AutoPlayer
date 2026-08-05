using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class ManagerSettingsStore
{
    public ManagerSettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(Protocol.DataRoot, "manager", "settings.json");
    }

    public string SettingsPath { get; }

    public ManagerSettings Load(out string warning)
    {
        warning = string.Empty;
        if (!File.Exists(SettingsPath))
        {
            return new ManagerSettings();
        }

        try
        {
            JObject document = JObject.Parse(File.ReadAllText(SettingsPath));
            ManagerSettings settings = document.ToObject<ManagerSettings>() ?? new ManagerSettings();
            bool hasSpeedOverrideSetting = document.Properties().Any(property =>
                string.Equals(property.Name, nameof(ManagerSettings.OverrideGameSpeed), StringComparison.OrdinalIgnoreCase));
            bool hasLegacySpeedState = document.Properties().Any(property =>
                string.Equals(property.Name, nameof(ManagerSettings.SpeedState), StringComparison.OrdinalIgnoreCase));
            if (!hasSpeedOverrideSetting && hasLegacySpeedState)
            {
                settings.OverrideGameSpeed = true;
                settings.SpeedState = 0;
                warning = "已将旧版自动设置的 3x 倍速迁移为 1x 常速，避免自动游玩时帧率过低。";
            }
            settings.NormalizeUpdateSource();
            return settings;
        }
        catch (Exception exception)
        {
            warning = "无法读取 Manager 设置。详细信息：" + exception.Message;
            return new ManagerSettings();
        }
    }

    public void Save(ManagerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.NormalizeUpdateSource();
        AtomicFile.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }
}
