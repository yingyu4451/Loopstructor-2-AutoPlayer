using System.Globalization;
using System.Text;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class BepInExConfigWriter
{
    public const string PluginConfigFileName = "com.ponegames.loopstructor2.autoplayer.cfg";

    public string Write(
        string gameRoot,
        string expectedAssemblySha256,
        float tickIntervalSeconds = 0.75f,
        int maxConsecutiveFailures = 8,
        float stallTimeoutSeconds = 90f)
    {
        if (expectedAssemblySha256.Length != 64 || expectedAssemblySha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("ExpectedAssemblySha256 must be a 64-character SHA-256 value.", nameof(expectedAssemblySha256));
        }

        string path = Path.Combine(
            Path.GetFullPath(gameRoot),
            "BepInEx",
            "config",
            PluginConfigFileName);
        StringBuilder content = new();
        content.AppendLine("# Managed by Loopstructor AutoPlayer Manager.");
        content.AppendLine("# Automation still requires a valid one-time launch activation.");
        content.AppendLine();
        content.AppendLine("[Runtime]");
        content.Append("TickIntervalSeconds = ").AppendLine(tickIntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        content.Append("MaxConsecutiveFailures = ").AppendLine(Math.Max(1, maxConsecutiveFailures).ToString(CultureInfo.InvariantCulture));
        content.Append("StallTimeoutSeconds = ").AppendLine(Math.Max(15f, stallTimeoutSeconds).ToString("0.###", CultureInfo.InvariantCulture));
        content.AppendLine();
        content.AppendLine("[Compatibility]");
        content.Append("ExpectedAssemblySha256 = ").AppendLine(expectedAssemblySha256.ToLowerInvariant());
        AtomicFile.WriteAllText(path, content.ToString());
        return path;
    }
}
