using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class BepInExInstallerFileSystemTests
{
    private static readonly string[] RuntimeFiles =
    {
        "winhttp.dll",
        "doorstop_config.ini",
        Path.Combine("BepInEx", "core", "BepInEx.dll"),
        Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll")
    };

    [Fact]
    public async Task InstallAndUninstall_PreserveGameAssemblyRuntimeAndThirdPartyPlugins()
    {
        using TemporaryDirectory temporary = new();
        string distributionRoot = Path.Combine(temporary.Root, "distribution");
        string gameRoot = Path.Combine(temporary.Root, "game");
        CreateRuntimePayload(distributionRoot);
        CreatePluginPayload(distributionRoot, includeCore: true);

        string assemblyPath = Path.Combine(gameRoot, "Skyspine_Data", "Managed", "Assembly-CSharp.dll");
        byte[] assemblyContent = Encoding.UTF8.GetBytes("fake-game-assembly-content");
        WriteBytes(assemblyPath, assemblyContent);
        string assemblyHash = HashFile(assemblyPath);

        string thirdPartyPlugin = Path.Combine(gameRoot, "BepInEx", "plugins", "ThirdParty", "ThirdParty.Plugin.dll");
        byte[] thirdPartyContent = Encoding.UTF8.GetBytes("third-party-plugin");
        WriteBytes(thirdPartyPlugin, thirdPartyContent);

        BepInExInstaller installer = new(CreateLayout(distributionRoot));
        PluginOperationResult install = await installer.InstallAsync(CreateGame(gameRoot, assemblyPath, assemblyHash));

        Assert.True(install.Success, install.Message);
        Assert.NotNull(install.Status);
        Assert.Equal(PluginState.Enabled, install.Status.State);
        Assert.True(install.Status.BepInExPresent);
        Assert.True(install.Status.BepInExCompatible);
        Assert.Equal(assemblyHash, HashFile(assemblyPath));
        Assert.Equal(assemblyContent, File.ReadAllBytes(assemblyPath));
        Assert.Equal(thirdPartyContent, File.ReadAllBytes(thirdPartyPlugin));

        foreach (string relative in RuntimeFiles)
        {
            string payload = Path.Combine(distributionRoot, "payload", "bepinex", relative);
            string installed = Path.Combine(gameRoot, relative);
            Assert.True(File.Exists(installed), "Missing installed runtime file: " + relative);
            Assert.Equal(HashFile(payload), HashFile(installed));
        }

        string autoPlayerDirectory = Path.Combine(gameRoot, "BepInEx", "plugins", "Loopstructor.AutoPlayer");
        string foreignFile = Path.Combine(autoPlayerDirectory, "ThirdParty.Readme.txt");
        byte[] foreignContent = Encoding.UTF8.GetBytes("not-owned-by-autoplayer");
        WriteBytes(foreignFile, foreignContent);

        PluginOperationResult uninstall = installer.Uninstall(gameRoot);

        Assert.True(uninstall.Success, uninstall.Message);
        Assert.NotNull(uninstall.Status);
        Assert.Equal(PluginState.NotInstalled, uninstall.Status.State);
        Assert.False(File.Exists(Path.Combine(autoPlayerDirectory, "Loopstructor.AutoPlayer.Plugin.dll")));
        Assert.False(File.Exists(Path.Combine(autoPlayerDirectory, "Loopstructor.AutoPlayer.Core.dll")));
        Assert.Equal(foreignContent, File.ReadAllBytes(foreignFile));
        Assert.Equal(thirdPartyContent, File.ReadAllBytes(thirdPartyPlugin));
        Assert.Equal(assemblyHash, HashFile(assemblyPath));
        Assert.Equal(assemblyContent, File.ReadAllBytes(assemblyPath));

        foreach (string relative in RuntimeFiles)
        {
            Assert.True(File.Exists(Path.Combine(gameRoot, relative)), "Uninstall removed runtime file: " + relative);
        }
    }

    [Fact]
    public async Task InstallAsync_IncompletePayloadLeavesExistingPluginDirectoryUnchanged()
    {
        using TemporaryDirectory temporary = new();
        string distributionRoot = Path.Combine(temporary.Root, "distribution");
        string gameRoot = Path.Combine(temporary.Root, "game");
        CreateRuntimePayload(distributionRoot);
        CreatePluginPayload(distributionRoot, includeCore: false);

        string assemblyPath = Path.Combine(gameRoot, "Skyspine_Data", "Managed", "Assembly-CSharp.dll");
        WriteBytes(assemblyPath, Encoding.UTF8.GetBytes("unchanged-game-assembly"));
        string assemblyHash = HashFile(assemblyPath);
        string pluginDirectory = Path.Combine(gameRoot, "BepInEx", "plugins", "Loopstructor.AutoPlayer");
        WriteBytes(Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.dll"), Encoding.UTF8.GetBytes("old-plugin"));
        WriteBytes(Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Core.dll"), Encoding.UTF8.GetBytes("old-core"));
        WriteBytes(Path.Combine(pluginDirectory, "local-note.txt"), Encoding.UTF8.GetBytes("keep-this-file"));
        string[] before = Snapshot(pluginDirectory);

        BepInExInstaller installer = new(CreateLayout(distributionRoot));
        PluginOperationResult result = await installer.InstallAsync(CreateGame(gameRoot, assemblyPath, assemblyHash));

        Assert.False(result.Success);
        Assert.Contains("Loopstructor.AutoPlayer.Core.dll", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(pluginDirectory));
        Assert.Equal(assemblyHash, HashFile(assemblyPath));
        string pluginParent = Path.GetDirectoryName(pluginDirectory)!;
        Assert.Empty(Directory.GetDirectories(pluginParent, ".Loopstructor.AutoPlayer.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InstallAsync_ConfigWriteFailureRestoresExistingPluginDirectory()
    {
        using TemporaryDirectory temporary = new();
        string distributionRoot = Path.Combine(temporary.Root, "distribution");
        string gameRoot = Path.Combine(temporary.Root, "game");
        CreateRuntimePayload(distributionRoot);
        CreatePluginPayload(distributionRoot, includeCore: true);

        string assemblyPath = Path.Combine(gameRoot, "Skyspine_Data", "Managed", "Assembly-CSharp.dll");
        WriteBytes(assemblyPath, Encoding.UTF8.GetBytes("rollback-game-assembly"));
        string assemblyHash = HashFile(assemblyPath);
        string pluginDirectory = Path.Combine(gameRoot, "BepInEx", "plugins", "Loopstructor.AutoPlayer");
        WriteBytes(Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.dll"), Encoding.UTF8.GetBytes("old-plugin"));
        WriteBytes(Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Core.dll"), Encoding.UTF8.GetBytes("old-core"));
        WriteBytes(Path.Combine(pluginDirectory, "old-version-note.txt"), Encoding.UTF8.GetBytes("keep-old-version"));
        string[] before = Snapshot(pluginDirectory);

        string configBlocker = Path.Combine(gameRoot, "BepInEx", "config");
        byte[] blockerContent = Encoding.UTF8.GetBytes("this-file-blocks-the-config-directory");
        WriteBytes(configBlocker, blockerContent);

        BepInExInstaller installer = new(CreateLayout(distributionRoot));
        PluginOperationResult result = await installer.InstallAsync(CreateGame(gameRoot, assemblyPath, assemblyHash));

        Assert.False(result.Success);
        Assert.Contains("已恢复原插件", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(pluginDirectory));
        Assert.Equal(blockerContent, File.ReadAllBytes(configBlocker));
        Assert.Equal(assemblyHash, HashFile(assemblyPath));
        string pluginParent = Path.GetDirectoryName(pluginDirectory)!;
        Assert.Empty(Directory.GetDirectories(pluginParent, ".Loopstructor.AutoPlayer.*", SearchOption.TopDirectoryOnly));
    }

    private static DistributionLayout CreateLayout(string distributionRoot)
    {
        ConstructorInfo constructor = typeof(DistributionLayout).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string) },
            modifiers: null) ?? throw new InvalidOperationException("DistributionLayout constructor was not found.");
        return (DistributionLayout)constructor.Invoke(new object[] { distributionRoot });
    }

    private static GameInstallValidation CreateGame(string gameRoot, string assemblyPath, string assemblyHash) => new()
    {
        GameRoot = gameRoot,
        AssemblyPath = assemblyPath,
        AssemblySha256 = assemblyHash
    };

    private static void CreateRuntimePayload(string distributionRoot)
    {
        string payloadRoot = Path.Combine(distributionRoot, "payload", "bepinex");
        foreach (string relative in RuntimeFiles)
        {
            WriteBytes(Path.Combine(payloadRoot, relative), Encoding.UTF8.GetBytes("pinned-runtime:" + relative.Replace('\\', '/')));
        }
    }

    private static void CreatePluginPayload(string distributionRoot, bool includeCore)
    {
        string payloadRoot = Path.Combine(distributionRoot, "payload", "plugin");
        WriteBytes(
            Path.Combine(payloadRoot, "Loopstructor.AutoPlayer.Plugin.dll"),
            Encoding.UTF8.GetBytes("new-plugin"));
        if (includeCore)
        {
            WriteBytes(
                Path.Combine(payloadRoot, "Loopstructor.AutoPlayer.Core.dll"),
                Encoding.UTF8.GetBytes("new-core"));
        }
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + HashFile(path))
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "Loopstructor.AutoPlayer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
