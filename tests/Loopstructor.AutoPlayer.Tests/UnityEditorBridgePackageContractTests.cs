using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UnityEditorBridgePackageContractTests
{
    [Fact]
    public void EmbeddedPackage_IsEditorOnlyAndDoesNotInjectPlayerLoaderDependencies()
    {
        string root = FindRepositoryRoot();
        string packageRoot = Path.Combine(root, "resources", "unity-package", "com.loopstructor.qa-editor-bridge");
        JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        JObject assembly = JObject.Parse(File.ReadAllText(Path.Combine(packageRoot, "Editor", "Loopstructor.QA.EditorBridge.asmdef")));
        string source = string.Join(
            "\n",
            Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.Equal("com.loopstructor.qa-editor-bridge", manifest.Value<string>("name"));
        Assert.Equal(new[] { "Editor" }, assembly["includePlatforms"]?.Values<string>());
        Assert.DoesNotContain("BepInEx", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnityEngine.Debug", source, StringComparison.Ordinal);
        Assert.Contains("/api/command", source, StringComparison.Ordinal);
        Assert.Contains("Authorization", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedPackageVersion_MatchesEveryProductVersionSurface()
    {
        string root = FindRepositoryRoot();
        string version = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants("VersionPrefix")
            .Single()
            .Value;
        string pluginInfo = File.ReadAllText(Path.Combine(root, "src", "Loopstructor.AutoPlayer.Plugin", "PluginInfo.cs"));
        string pluginVersion = Regex.Match(pluginInfo, "Version = \\\"(?<value>[^\\\"]+)\\\"").Groups["value"].Value;
        string desktopVersion = JObject.Parse(File.ReadAllText(Path.Combine(root, "desktop", "package.json"))).Value<string>("version")!;
        string bridgeVersion = JObject.Parse(File.ReadAllText(Path.Combine(
            root,
            "resources",
            "unity-package",
            "com.loopstructor.qa-editor-bridge",
            "package.json"))).Value<string>("version")!;

        Assert.Equal("0.6.72", version);
        Assert.Equal(version, pluginVersion);
        Assert.Equal(version, desktopVersion);
        Assert.Equal(version, bridgeVersion);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor != null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "Loopstructor.AutoPlayer.sln"))) return cursor.FullName;
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
