using System.Reflection;
using System.Text;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UnityProjectBridgeInstallerTests
{
    [Fact]
    public void InspectInstallUpdateAndUninstall_ManageOnlyTheOwnedEditorPackage()
    {
        using TemporaryDirectory temporary = new();
        string distributionRoot = Path.Combine(temporary.Root, "distribution");
        string projectRoot = Path.Combine(temporary.Root, "Loopstructor2");
        CreateDistribution(distributionRoot);
        CreateUnityProject(projectRoot);
        UnityProjectBridgeInstaller installer = new(CreateLayout(distributionRoot));

        var before = installer.Inspect(projectRoot);

        Assert.True(before.Valid, before.Message);
        Assert.Equal("2022.3.62f3c1", before.UnityVersion);
        Assert.False(before.BridgeInstalled);

        var installed = installer.Install(projectRoot);
        string packageRoot = Path.Combine(projectRoot, "Packages", UnityProjectBridgeInstaller.PackageName);
        string managedRoot = Path.Combine(packageRoot, "Editor", "Managed");

        Assert.True(installed.Success, installed.Message);
        Assert.Equal("Editor 连接组件已安装。重新聚焦 Unity，等待脚本编译完成。", installed.Message);
        Assert.True(File.Exists(Path.Combine(packageRoot, "Editor", "QaBridgeBootstrap.cs")));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(Path.Combine(packageRoot, "package.json"))[..3]);
        Assert.Equal("editor-runtime", File.ReadAllText(Path.Combine(managedRoot, "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll")));
        Assert.Equal("core", File.ReadAllText(Path.Combine(managedRoot, "Loopstructor.AutoPlayer.Core.dll")));
        Assert.False(File.Exists(Path.Combine(managedRoot, "BepInEx.dll")));
        Assert.False(File.Exists(Path.Combine(managedRoot, "Loopstructor.AutoPlayer.Plugin.dll")));
        Assert.False(File.Exists(Path.Combine(managedRoot, "0Harmony.dll")));
        Assert.False(File.Exists(Path.Combine(managedRoot, "Newtonsoft.Json.dll")));
        Assert.True(installer.Inspect(projectRoot).BridgeInstalled);

        File.WriteAllText(Path.Combine(packageRoot, "stale.txt"), "stale", Encoding.UTF8);
        var updated = installer.Install(projectRoot);
        Assert.True(updated.Success, updated.Message);
        Assert.Equal("Editor 连接组件已更新。重新聚焦 Unity，等待脚本编译完成。", updated.Message);
        Assert.False(File.Exists(Path.Combine(packageRoot, "stale.txt")));

        var removed = installer.Uninstall(projectRoot);
        Assert.True(removed.Success, removed.Message);
        Assert.False(Directory.Exists(packageRoot));
    }

    [Fact]
    public void InstallAndUninstall_RefuseAForeignPackageAtTheOwnedPath()
    {
        using TemporaryDirectory temporary = new();
        string distributionRoot = Path.Combine(temporary.Root, "distribution");
        string projectRoot = Path.Combine(temporary.Root, "Loopstructor2");
        CreateDistribution(distributionRoot);
        CreateUnityProject(projectRoot);
        string packageRoot = Path.Combine(projectRoot, "Packages", UnityProjectBridgeInstaller.PackageName);
        WriteText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"com.example.foreign\"}");
        UnityProjectBridgeInstaller installer = new(CreateLayout(distributionRoot));

        var install = installer.Install(projectRoot);
        var uninstall = installer.Uninstall(projectRoot);

        Assert.False(install.Success);
        Assert.False(uninstall.Success);
        Assert.True(File.Exists(Path.Combine(packageRoot, "package.json")));
    }

    private static void CreateDistribution(string root)
    {
        string source = Path.Combine(root, "resources", "unity-package", UnityProjectBridgeInstaller.PackageName);
        WriteText(Path.Combine(source, "package.json"), "{\"name\":\"com.loopstructor.qa-editor-bridge\"}");
        WriteText(Path.Combine(source, "Editor", "QaBridgeBootstrap.cs"), "bridge");
        WriteText(Path.Combine(root, "payload", "editor", "Loopstructor.AutoPlayer.EditorBridge.Runtime.dll"), "editor-runtime");
        WriteText(Path.Combine(root, "payload", "editor", "Loopstructor.AutoPlayer.Core.dll"), "core");
        WriteText(Path.Combine(root, "payload", "editor", "BepInEx.dll"), "do-not-copy");
        WriteText(Path.Combine(root, "payload", "bepinex", "BepInEx", "core", "0Harmony.dll"), "do-not-copy");
        WriteText(Path.Combine(root, "payload", "plugin", "Newtonsoft.Json.dll"), "do-not-copy");
    }

    private static void CreateUnityProject(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        WriteText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f3c1\n");
    }

    private static DistributionLayout CreateLayout(string root)
    {
        ConstructorInfo constructor = typeof(DistributionLayout).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null) ?? throw new InvalidOperationException("DistributionLayout constructor was not found.");
        return (DistributionLayout)constructor.Invoke(new object[] { root });
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "Loopstructor.EditorBridge.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
