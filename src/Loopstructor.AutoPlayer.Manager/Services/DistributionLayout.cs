namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class DistributionLayout
{
    private DistributionLayout(string root)
    {
        Root = root;
        PayloadRoot = Path.Combine(root, "payload");
        BepInExPayloadRoot = Path.Combine(PayloadRoot, "bepinex");
        PluginPayloadRoot = Path.Combine(PayloadRoot, "plugin");
        UpdaterExecutable = Path.Combine(root, "updater", "Loopstructor.AutoPlayer.Updater.exe");
        ManagerExecutable = Path.Combine(root, "Loopstructor.AutoPlayer.Manager.exe");
    }

    public string Root { get; }
    public string PayloadRoot { get; }
    public string BepInExPayloadRoot { get; }
    public string PluginPayloadRoot { get; }
    public string UpdaterExecutable { get; }
    public string ManagerExecutable { get; }

    public static DistributionLayout Locate(string? baseDirectory = null)
    {
        string? configured = Environment.GetEnvironmentVariable("LOOPSTRUCTOR_AUTOPLAYER_DISTRIBUTION_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new DistributionLayout(Path.GetFullPath(configured));
        }

        DirectoryInfo? cursor = new(Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory));
        for (int depth = 0; cursor != null && depth < 7; depth++, cursor = cursor.Parent)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "payload"))
                || (string.Equals(cursor.Name, "manager", StringComparison.OrdinalIgnoreCase)
                    && cursor.Parent != null
                    && Directory.Exists(Path.Combine(cursor.Parent.FullName, "payload"))))
            {
                string root = string.Equals(cursor.Name, "manager", StringComparison.OrdinalIgnoreCase)
                    ? cursor.Parent!.FullName
                    : cursor.FullName;
                return new DistributionLayout(root);
            }
        }

        DirectoryInfo applicationDirectory = new(Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory));
        string fallback = string.Equals(applicationDirectory.Name, "manager", StringComparison.OrdinalIgnoreCase)
            ? applicationDirectory.Parent?.FullName ?? applicationDirectory.FullName
            : applicationDirectory.FullName;
        return new DistributionLayout(fallback);
    }
}
