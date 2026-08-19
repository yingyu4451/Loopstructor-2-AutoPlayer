using System.Diagnostics;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public void TryResolveCoordinates_DefaultSettingsUsePublishedRepository()
    {
        WithGitHubEnvironment(null, null, () =>
        {
            bool configured = UpdateCoordinator.TryResolveCoordinates(
                new ManagerSettings(),
                out string owner,
                out string repository);

            Assert.True(configured);
            Assert.Equal("yingyu4451", owner);
            Assert.Equal("Loopstructor-2-AutoPlayer", repository);
        });
    }

    [Fact]
    public void TryResolveCoordinates_EmptyLegacySettingsUsePublishedRepository()
    {
        WithGitHubEnvironment(null, null, () =>
        {
            ManagerSettings settings = new()
            {
                GitHubOwner = string.Empty,
                GitHubRepository = " "
            };

            bool configured = UpdateCoordinator.TryResolveCoordinates(settings, out string owner, out string repository);

            Assert.True(configured);
            Assert.Equal("yingyu4451", owner);
            Assert.Equal("Loopstructor-2-AutoPlayer", repository);
        });
    }

    [Fact]
    public void TryResolveCoordinates_RenamedPublishedRepositoryMigratesSavedLegacyPair()
    {
        WithGitHubEnvironment(null, null, () =>
        {
            ManagerSettings settings = new()
            {
                GitHubOwner = "yingyu4451",
                GitHubRepository = "gui2"
            };

            bool configured = UpdateCoordinator.TryResolveCoordinates(settings, out string owner, out string repository);

            Assert.True(configured);
            Assert.Equal("yingyu4451", owner);
            Assert.Equal("Loopstructor-2-AutoPlayer", repository);
        });
    }

    [Fact]
    public void TryResolveCoordinates_ExplicitSettingsOverridePublishedRepository()
    {
        WithGitHubEnvironment(null, null, () =>
        {
            ManagerSettings settings = new()
            {
                GitHubOwner = " alternate-owner ",
                GitHubRepository = " alternate-repository "
            };

            bool configured = UpdateCoordinator.TryResolveCoordinates(settings, out string owner, out string repository);

            Assert.True(configured);
            Assert.Equal("alternate-owner", owner);
            Assert.Equal("alternate-repository", repository);
        });
    }

    [Fact]
    public void TryResolveCoordinates_EnvironmentOverridesExplicitSettings()
    {
        WithGitHubEnvironment("environment-owner", "environment-repository", () =>
        {
            ManagerSettings settings = new()
            {
                GitHubOwner = "configured-owner",
                GitHubRepository = "configured-repository"
            };

            bool configured = UpdateCoordinator.TryResolveCoordinates(settings, out string owner, out string repository);

            Assert.True(configured);
            Assert.Equal("environment-owner", owner);
            Assert.Equal("environment-repository", repository);
        });
    }

    [Fact]
    public void TryResolveCoordinates_InvalidEnvironmentOverrideFailsClosed()
    {
        WithGitHubEnvironment("invalid/owner", "Loopstructor-2-AutoPlayer", () =>
        {
            bool configured = UpdateCoordinator.TryResolveCoordinates(
                new ManagerSettings(),
                out _,
                out _);

            Assert.False(configured);
        });
    }

    [Fact]
    public void InterpretCheckResult_NonzeroExitRejectsSuccessfulJson()
    {
        string output = JsonConvert.SerializeObject(new ManagerUpdateStatus
        {
            Success = true,
            UpdateAvailable = true,
            CurrentVersion = "1.0.0",
            LatestVersion = "2.0.0",
            Message = "AutoPlayer 2.0.0 可用。"
        });

        ManagerUpdateStatus result = UpdateCoordinator.InterpretCheckResult(
            exitCode: 23,
            output,
            error: string.Empty,
            currentVersion: "1.0.0");

        Assert.False(result.Success);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.Contains("退出代码为 23", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InterpretCheckResult_ZeroExitAcceptsSuccessfulJson()
    {
        string output = JsonConvert.SerializeObject(new ManagerUpdateStatus
        {
            Success = true,
            UpdateAvailable = true,
            CurrentVersion = "1.0.0",
            LatestVersion = "2.0.0",
            Message = "AutoPlayer 2.0.0 可用。"
        });

        ManagerUpdateStatus result = UpdateCoordinator.InterpretCheckResult(
            exitCode: 0,
            output,
            error: string.Empty,
            currentVersion: "1.0.0");

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersion);
    }

    [Fact]
    public void TryCreateInvocation_UsesManagerDirectoryUpdater()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerTests",
            Guid.NewGuid().ToString("N"));
        string managerDirectory = Path.Combine(root, "manager");
        Directory.CreateDirectory(managerDirectory);
        try
        {
            string sharedUpdaterExecutable = Path.Combine(managerDirectory, "Loopstructor.AutoPlayer.Updater.exe");
            File.WriteAllBytes(sharedUpdaterExecutable, Array.Empty<byte>());
            UpdateCoordinator coordinator = new(DistributionLayout.Locate(root));

            bool created = coordinator.TryCreateInvocation(out ProcessStartInfo startInfo, "check");

            Assert.True(created);
            Assert.Equal(sharedUpdaterExecutable, startInfo.FileName);
            Assert.Equal(managerDirectory, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.Equal(new[] { "check" }, startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryCreateInvocation_RetiredUpdaterDirectoryAssemblyIsRejected()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerTests",
            Guid.NewGuid().ToString("N"));
        string updaterDirectory = Path.Combine(root, "updater");
        Directory.CreateDirectory(updaterDirectory);
        try
        {
            File.WriteAllBytes(
                Path.Combine(updaterDirectory, "Loopstructor.AutoPlayer.Updater.dll"),
                Array.Empty<byte>());
            UpdateCoordinator coordinator = new(DistributionLayout.Locate(root));

            bool created = coordinator.TryCreateInvocation(out _, "check");

            Assert.False(created);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryCreateInvocation_RetiredUpdaterDirectoryExecutableIsRejected()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerTests",
            Guid.NewGuid().ToString("N"));
        string updaterDirectory = Path.Combine(root, "updater");
        Directory.CreateDirectory(updaterDirectory);
        try
        {
            string updaterExecutable = Path.Combine(updaterDirectory, "Loopstructor.AutoPlayer.Updater.exe");
            File.WriteAllBytes(updaterExecutable, Array.Empty<byte>());
            UpdateCoordinator coordinator = new(DistributionLayout.Locate(root));

            bool created = coordinator.TryCreateInvocation(out _, "apply");

            Assert.False(created);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WithGitHubEnvironment(string? owner, string? repository, Action assertion)
    {
        string? previousOwner = Environment.GetEnvironmentVariable(UpdateCoordinator.GitHubOwnerEnvironmentVariable);
        string? previousRepository = Environment.GetEnvironmentVariable(UpdateCoordinator.GitHubRepositoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateCoordinator.GitHubOwnerEnvironmentVariable, owner);
            Environment.SetEnvironmentVariable(UpdateCoordinator.GitHubRepositoryEnvironmentVariable, repository);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateCoordinator.GitHubOwnerEnvironmentVariable, previousOwner);
            Environment.SetEnvironmentVariable(UpdateCoordinator.GitHubRepositoryEnvironmentVariable, previousRepository);
        }
    }
}
