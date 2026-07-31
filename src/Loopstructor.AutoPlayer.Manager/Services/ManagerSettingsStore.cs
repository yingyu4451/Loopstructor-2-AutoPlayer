using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

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
            ManagerSettings settings = JsonConvert.DeserializeObject<ManagerSettings>(File.ReadAllText(SettingsPath))
                                       ?? new ManagerSettings();
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
