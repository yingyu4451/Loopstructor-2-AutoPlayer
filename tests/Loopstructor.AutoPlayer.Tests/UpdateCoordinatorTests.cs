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
            Assert.Equal("gui2", repository);
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
            Assert.Equal("gui2", repository);
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
        WithGitHubEnvironment("invalid/owner", "gui2", () =>
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
            Message = "AutoPlayer 2.0.0 is available."
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
        Assert.Contains("exited with code 23", result.Message, StringComparison.OrdinalIgnoreCase);
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
            Message = "AutoPlayer 2.0.0 is available."
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
