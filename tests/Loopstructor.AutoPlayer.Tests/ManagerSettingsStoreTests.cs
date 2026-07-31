using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerSettingsStoreTests
{
    [Fact]
    public void Load_MigratesBlankLegacyUpdateSourceToPublishedRepository()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "GitHubOwner": "",
                  "GitHubRepository": " "
                }
                """);

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(ManagerSettings.DefaultGitHubOwner, settings.GitHubOwner);
            Assert.Equal(ManagerSettings.DefaultGitHubRepository, settings.GitHubRepository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_MigratesNullLegacyUpdateSourceToPublishedRepository()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "GitHubOwner": null,
                  "GitHubRepository": null
                }
                """);

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(ManagerSettings.DefaultGitHubOwner, settings.GitHubOwner);
            Assert.Equal(ManagerSettings.DefaultGitHubRepository, settings.GitHubRepository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_TrimsAndPreservesCustomUpdateSource()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "GitHubOwner": " custom-owner ",
                  "GitHubRepository": " custom-repository "
                }
                """);

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal("custom-owner", settings.GitHubOwner);
            Assert.Equal("custom-repository", settings.GitHubRepository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_ReplacesBlankUpdateSourceWithPublishedRepository()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            ManagerSettingsStore store = new(settingsPath);
            ManagerSettings settings = new()
            {
                GitHubOwner = string.Empty,
                GitHubRepository = string.Empty
            };

            store.Save(settings);
            ManagerSettings reloaded = store.Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(ManagerSettings.DefaultGitHubOwner, reloaded.GitHubOwner);
            Assert.Equal(ManagerSettings.DefaultGitHubRepository, reloaded.GitHubRepository);
            string savedJson = File.ReadAllText(settingsPath);
            Assert.Contains("\"GitHubOwner\": \"yingyu4451\"", savedJson);
            Assert.Contains("\"GitHubRepository\": \"gui2\"", savedJson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "LoopstructorAutoPlayerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
