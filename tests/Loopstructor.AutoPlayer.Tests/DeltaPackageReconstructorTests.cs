using System.Security.Cryptography;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class DeltaPackageReconstructorTests
{
    [Fact]
    public void ReconstructsCompleteRelease_AndTransactionalInstallPreservesConfiguration()
    {
        using DeltaFixture fixture = new();
        string current = fixture.CreateRelease("current", "0.5.2", release =>
        {
            fixture.WriteFile(release, "manager", "feature.dat", "old-feature");
            fixture.WriteFile(release, "shared", "runtime.dat", new string('r', 512 * 1024));
            fixture.WriteFile(release, "obsolete.dat", "remove-me");
        });
        File.WriteAllText(Path.Combine(current, "autoplayer-update.json"), "{\"configured\":true}");
        string target = fixture.CreateRelease("target", "0.5.3", release =>
        {
            fixture.WriteFile(release, "manager", "feature.dat", "new-feature");
            fixture.WriteFile(release, "shared", "runtime.dat", new string('r', 512 * 1024));
            fixture.WriteFile(release, "payload", "plugin", "new-feature.dat", "added");
        });
        string delta = fixture.CreateDelta(current, target);
        string staging = Path.Combine(fixture.Root, ".LoopstructorAutoPlayer-staging-test");

        new DeltaPackageReconstructor().Reconstruct(
            delta,
            current,
            staging,
            "0.5.2",
            "0.5.3");

        new ReleasePackageValidator().Validate(staging, "0.5.3");
        Assert.Equal("new-feature", File.ReadAllText(Path.Combine(staging, "manager", "feature.dat")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(staging, "payload", "plugin", "new-feature.dat")));
        Assert.False(File.Exists(Path.Combine(staging, "obsolete.dat")));
        Assert.False(File.Exists(Path.Combine(staging, "autoplayer-update.json")));

        TransactionalInstaller installer = new(journalPath: Path.Combine(fixture.Root, "transaction.json"));
        string backup = installer.Apply(staging, current, "0.5.3");

        Assert.True(File.Exists(Path.Combine(current, "autoplayer-update.json")));
        Assert.False(File.Exists(Path.Combine(current, "obsolete.dat")));
        Assert.Equal(string.Empty, backup);
        Assert.Equal("0.5.3", ReleasePackageValidator.ReadMarker(current).Version);
        Assert.Empty(Directory.GetDirectories(
            fixture.Root,
            ".LoopstructorAutoPlayer-rollback-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void MissingChangedPayload_IsRejectedWithoutChangingCurrentRelease()
    {
        using DeltaFixture fixture = new();
        string current = fixture.CreateRelease("current", "0.5.2", release =>
            fixture.WriteFile(release, "manager", "feature.dat", "old"));
        string target = fixture.CreateRelease("target", "0.5.3", release =>
            fixture.WriteFile(release, "manager", "feature.dat", "new"));
        string delta = fixture.CreateDelta(current, target);
        File.Delete(Path.Combine(delta, "files", "manager", "feature.dat"));
        string staging = Path.Combine(fixture.Root, ".LoopstructorAutoPlayer-staging-test");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new DeltaPackageReconstructor().Reconstruct(
                delta,
                current,
                staging,
                "0.5.2",
                "0.5.3"));

        Assert.Contains("缺少变更文件", exception.Message, StringComparison.Ordinal);
        Assert.Equal("0.5.2", ReleasePackageValidator.ReadMarker(current).Version);
        Assert.Equal("old", File.ReadAllText(Path.Combine(current, "manager", "feature.dat")));
    }

    [Fact]
    public void BaseVersionMatching_IsExactIncludingBuildMetadata()
    {
        using DeltaFixture fixture = new();
        string current = fixture.CreateRelease("current", "0.5.2+build1");
        string target = fixture.CreateRelease("target", "0.5.3");
        string delta = fixture.CreateDelta(current, target);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new DeltaPackageReconstructor().Reconstruct(
                delta,
                current,
                Path.Combine(fixture.Root, ".LoopstructorAutoPlayer-staging-test"),
                "0.5.2+build2",
                "0.5.3"));

        Assert.Contains("不完全一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetVersionMatching_IsExactIncludingBuildMetadata()
    {
        using DeltaFixture fixture = new();
        string current = fixture.CreateRelease("current", "0.5.2");
        string target = fixture.CreateRelease("target", "0.5.3+build1");
        string delta = fixture.CreateDelta(current, target);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new DeltaPackageReconstructor().Reconstruct(
                delta,
                current,
                Path.Combine(fixture.Root, ".LoopstructorAutoPlayer-staging-test"),
                "0.5.2",
                "0.5.3+build2"));

        Assert.Contains("目标版本", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不完全一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_UsesOnlyExactInstalledVersionAndSmallerAsset()
    {
        UpdateManifest manifest = new()
        {
            SchemaVersion = 2,
            Version = "0.5.3",
            RuntimeIdentifier = "win-x64",
            AssetName = "Loopstructor.AutoPlayer-0.5.3-win-x64.zip",
            Sha256 = new string('a', 64),
            Size = 1000
        };
        UpdateDeltaAsset deltaManifest = new()
        {
            FromVersion = "0.5.2+build1",
            AssetName = "Loopstructor.AutoPlayer-0.5.2+build1-to-0.5.3-win-x64.delta.zip",
            Sha256 = new string('b', 64),
            Size = 100
        };
        manifest.DeltaAssets.Add(deltaManifest);
        ResolvedDeltaPackage delta = new()
        {
            Manifest = deltaManifest,
            PackageAsset = new GitHubReleaseAsset
            {
                Name = deltaManifest.AssetName,
                DownloadUri = new Uri("https://github.com/yingyu4451/Loopstructor-2-QA-Tool/releases/download/v0.5.3/" + deltaManifest.AssetName),
                Size = deltaManifest.Size
            }
        };
        ResolvedUpdate update = new()
        {
            Manifest = manifest,
            PackageAsset = new GitHubReleaseAsset
            {
                Name = manifest.AssetName,
                DownloadUri = new Uri("https://github.com/yingyu4451/Loopstructor-2-QA-Tool/releases/download/v0.5.3/" + manifest.AssetName),
                Size = manifest.Size
            },
            DeltaPackages = new[] { delta },
            ReleaseTag = "v0.5.3"
        };

        Assert.Same(delta, Loopstructor.AutoPlayer.Updater.Program.SelectDeltaPackage(update, "0.5.2+build1"));
        Assert.Null(Loopstructor.AutoPlayer.Updater.Program.SelectDeltaPackage(update, "0.5.2+build2"));
        Assert.Null(Loopstructor.AutoPlayer.Updater.Program.SelectDeltaPackage(update, "0.5.1"));
        deltaManifest.Size = manifest.Size;
        Assert.Null(Loopstructor.AutoPlayer.Updater.Program.SelectDeltaPackage(update, "0.5.2+build1"));
        deltaManifest.Size = manifest.Size + 1;
        Assert.Null(Loopstructor.AutoPlayer.Updater.Program.SelectDeltaPackage(update, "0.5.2+build1"));
    }

    private sealed class DeltaFixture : IDisposable
    {
        public DeltaFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "autoplayer-delta-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateRelease(string name, string version, Action<string>? customize = null)
        {
            string root = Path.Combine(Root, name);
            WriteFile(root, "Loopstructor.AutoPlayer.Manager.exe", "launcher-" + version);
            WriteFile(root, "manager", "Loopstructor.AutoPlayer.Manager.exe", "manager-" + version);
            WriteFile(root, "manager", "Loopstructor.AutoPlayer.Updater.exe", "updater-" + version);
            WriteFile(root, "payload", "bepinex", "winhttp.dll", "loader");
            WriteFile(root, "payload", "bepinex", "doorstop_config.ini", "enabled=true");
            WriteFile(root, "payload", "bepinex", "BepInEx", "core", "BepInEx.dll", "5.4.23.5");
            WriteFile(root, "payload", "bepinex", "BepInEx", "core", "BepInEx.Preloader.dll", "5.4.23.5");
            WriteFile(root, "payload", "plugin", "Loopstructor.AutoPlayer.Plugin.dll", "plugin-" + version);
            WriteFile(root, "payload", "plugin", "Loopstructor.AutoPlayer.Core.dll", "core-" + version);
            File.WriteAllText(
                Path.Combine(root, "autoplayer-release.json"),
                JsonSerializer.Serialize(new ReleaseMarker
                {
                    Version = version,
                    BepInExVersion = "5.4.23.5",
                    ManagerPath = "Loopstructor.AutoPlayer.Manager.exe",
                    UpdaterPath = "manager/Loopstructor.AutoPlayer.Updater.exe",
                    BepInExPayloadPath = "payload/bepinex",
                    PluginPayloadPath = "payload/plugin"
                }));
            customize?.Invoke(root);
            WriteChecksums(root);
            return root;
        }

        public string CreateDelta(string current, string target)
        {
            string delta = Path.Combine(Root, "delta-" + Guid.NewGuid().ToString("N"));
            string payload = Path.Combine(delta, "files");
            Directory.CreateDirectory(payload);
            File.Copy(Path.Combine(target, "checksums.sha256"), Path.Combine(delta, "checksums.sha256"));
            IReadOnlyDictionary<string, ReleaseChecksumEntry> currentCatalog =
                ReleasePackageValidator.ReadChecksumCatalog(Path.Combine(current, "checksums.sha256"));
            IReadOnlyDictionary<string, ReleaseChecksumEntry> targetCatalog =
                ReleasePackageValidator.ReadChecksumCatalog(Path.Combine(target, "checksums.sha256"));
            foreach (ReleaseChecksumEntry entry in targetCatalog.Values)
            {
                if (currentCatalog.TryGetValue(entry.RelativePath, out ReleaseChecksumEntry? currentEntry)
                    && string.Equals(currentEntry.Sha256, entry.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }

                string destination = Path.Combine(payload, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(target, entry.RelativePath), destination);
            }

            return delta;
        }

        public void WriteFile(string root, params string[] pathAndContent)
        {
            string content = pathAndContent[^1];
            string[] path = pathAndContent[..^1];
            string file = Path.Combine(new[] { root }.Concat(path).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static void WriteChecksums(string root)
        {
            string checksumPath = Path.Combine(root, "checksums.sha256");
            if (File.Exists(checksumPath)) File.Delete(checksumPath);
            string[] lines = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                {
                    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                    return hash + "  " + Path.GetRelativePath(root, path).Replace('\\', '/');
                })
                .ToArray();
            File.WriteAllText(checksumPath, string.Join('\n', lines) + "\n");
        }
    }
}
