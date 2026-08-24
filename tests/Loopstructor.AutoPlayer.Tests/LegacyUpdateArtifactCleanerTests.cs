using System.Text.Json;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class LegacyUpdateArtifactCleanerTests
{
    [Fact]
    public void CleanupAfterUpdate_RemovesOnlyStrictGeneratedReleaseDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-cleaner-tests-" + Guid.NewGuid().ToString("N"));
        string release = Path.Combine(root, "Loopstructor 2.AutoPlayer");
        string generated = Path.Combine(root, ".LoopstructorAutoPlayer-backup-20260824-120000-a1b2c3d4");
        string userDirectory = Path.Combine(root, ".LoopstructorAutoPlayer-backup-my-copy");
        try
        {
            CreateRelease(release);
            CreateRelease(generated);
            CreateRelease(userDirectory);

            IReadOnlyList<string> removed = LegacyUpdateArtifactCleaner.CleanupAfterUpdate(release);

            Assert.Contains(generated, removed, StringComparer.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(generated));
            Assert.True(Directory.Exists(userDirectory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupAfterUpdate_DoesNotRemoveReleaseWithInvalidChecksums()
    {
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-cleaner-tests-" + Guid.NewGuid().ToString("N"));
        string release = Path.Combine(root, "Loopstructor 2.AutoPlayer");
        string generated = Path.Combine(root, ".LoopstructorAutoPlayer-backup-20260824-120000-a1b2c3d4");
        try
        {
            CreateRelease(release);
            CreateRelease(generated);
            File.AppendAllText(Path.Combine(generated, "Loopstructor.AutoPlayer.Manager.exe"), "corrupt");

            IReadOnlyList<string> removed = LegacyUpdateArtifactCleaner.CleanupAfterUpdate(release);

            Assert.DoesNotContain(generated, removed, StringComparer.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(generated));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(".LoopstructorAutoPlayer-backup-20260824-120000-a1b2c3d4", ".LoopstructorAutoPlayer-backup-", true)]
    [InlineData(".LoopstructorAutoPlayer-rollback-20260824-120000-a1b2c3d4", ".LoopstructorAutoPlayer-rollback-", true)]
    [InlineData(".LoopstructorAutoPlayer-backup-my-copy", ".LoopstructorAutoPlayer-backup-", false)]
    [InlineData(".LoopstructorAutoPlayer-backup-20260824-120000-zzzzzzzz", ".LoopstructorAutoPlayer-backup-", false)]
    public void IsGeneratedName_RequiresTimestampAndHexIdentity(string name, string prefix, bool expected)
    {
        Assert.Equal(expected, LegacyUpdateArtifactCleaner.IsGeneratedName(name, prefix));
    }

    private static void CreateRelease(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "manager"));
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        string managerPath = Path.Combine(root, "Loopstructor.AutoPlayer.Manager.exe");
        string markerPath = Path.Combine(root, "autoplayer-release.json");
        File.WriteAllText(managerPath, "manager");
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new { version = "0.6.25" }));
        File.WriteAllLines(
            Path.Combine(root, "checksums.sha256"),
            new[]
            {
                HashLine(managerPath, "Loopstructor.AutoPlayer.Manager.exe"),
                HashLine(markerPath, "autoplayer-release.json")
            });
    }

    private static string HashLine(string path, string relativePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() + "  " + relativePath;
}
