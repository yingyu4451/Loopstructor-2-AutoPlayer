using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ManagerSettingsStoreTests
{
    [Fact]
    public void Load_LegacyNumericPriorityZeroMeansPreferVehicleRewards()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{\"DecisionPriority\":0}");

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(AutomationDecisionPriority.VehicleRewards, settings.DecisionPriority);
            Assert.Equal(0, (int)settings.DecisionPriority);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacySpeedStateDoesNotEnableAutomaticSpeedOverride()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{\"SpeedState\":2}");

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Contains("1x", warning);
            Assert.True(settings.OverrideGameSpeed);
            Assert.Equal(0, settings.SpeedState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
    public void Load_MigratesRenamedPublishedRepositoryWithoutChangingCustomForks()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "GitHubOwner": "yingyu4451",
                  "GitHubRepository": "gui2"
                }
                """);

            ManagerSettings settings = new ManagerSettingsStore(settingsPath).Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(ManagerSettings.DefaultGitHubOwner, settings.GitHubOwner);
            Assert.Equal(ManagerSettings.DefaultGitHubRepository, settings.GitHubRepository);

            settings.GitHubOwner = "custom-owner";
            settings.GitHubRepository = "gui2";
            settings.NormalizeUpdateSource();
            Assert.Equal("custom-owner", settings.GitHubOwner);
            Assert.Equal("gui2", settings.GitHubRepository);
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
            Assert.Contains("\"GitHubRepository\": \"Loopstructor-2-AutoPlayer\"", savedJson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_PreservesStoryAndDecisionSettings()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            ManagerSettingsStore store = new(settingsPath);
            ManagerSettings settings = new()
            {
                SkipStory = true,
                DecisionPriority = AutomationDecisionPriority.VehicleRewards
            };

            store.Save(settings);
            ManagerSettings reloaded = store.Load(out string warning);

            Assert.Empty(warning);
            Assert.True(reloaded.SkipStory);
            Assert.Equal(AutomationDecisionPriority.VehicleRewards, reloaded.DecisionPriority);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_PreservesUiScaleCharacterAndRelicPriority()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            ManagerSettingsStore store = new(settingsPath);
            ManagerSettings settings = new()
            {
                UiScaleMode = UiScaleMode.Custom,
                CustomUiScalePercent = 135,
                CharacterCfgIndex = 8,
                DecisionPriority = AutomationDecisionPriority.Relics
            };

            store.Save(settings);
            ManagerSettings reloaded = store.Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal(UiScaleMode.Custom, reloaded.UiScaleMode);
            Assert.Equal(135, reloaded.CustomUiScalePercent);
            Assert.Equal(8, reloaded.CharacterCfgIndex);
            Assert.Equal(AutomationDecisionPriority.Relics, reloaded.DecisionPriority);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_PreservesSupportedSkinAndMigratesLegacySkin()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            ManagerSettingsStore store = new(settingsPath);
            ManagerSettings settings = new() { SkinId = "skyspine" };

            store.Save(settings);
            ManagerSettings reloaded = store.Load(out string warning);

            Assert.Empty(warning);
            Assert.Equal("skyspine", reloaded.SkinId);
            reloaded.SkinId = "signal";
            store.Save(reloaded);
            Assert.Equal("skyspine", store.Load(out _).SkinId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyStartupUpdatePreference_IsIgnoredAndNotSavedAgain()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{\"CheckUpdatesOnStart\":false}");
            ManagerSettingsStore store = new(settingsPath);

            ManagerSettings settings = store.Load(out string warning);
            store.Save(settings);

            Assert.Empty(warning);
            Assert.DoesNotContain("CheckUpdatesOnStart", File.ReadAllText(settingsPath), StringComparison.Ordinal);
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
