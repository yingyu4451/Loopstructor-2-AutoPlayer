using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GameLauncherTests
{
    [Fact]
    public void CreateStartInfo_PinsSelectedBuildToSkyspineSteamApp()
    {
        string gameRoot = Path.Combine(Path.GetTempPath(), "Skyspine QA Build");
        string artifactRoot = Path.Combine(Path.GetTempPath(), "Skyspine QA Artifacts");
        GameInstallValidation game = new()
        {
            GameRoot = gameRoot,
            ExecutablePath = Path.Combine(gameRoot, "Loopstructor 2_ Skyspine.exe")
        };
        ActivationSession session = new()
        {
            GameRoot = gameRoot,
            TicketPath = Path.Combine(Path.GetTempPath(), "launch-ticket.json"),
            EnvironmentVariables = new Dictionary<string, string>
            {
                [Protocol.EnabledEnvironmentVariable] = "1",
                [Protocol.TokenEnvironmentVariable] = "test-token",
                [Protocol.CheatModeAllowedEnvironmentVariable] = "1"
            },
            Ticket = new LaunchTicket
            {
                ArtifactRoot = artifactRoot,
                ProfileRoot = Path.Combine(Path.GetTempPath(), "Skyspine QA Profile")
            }
        };

        System.Diagnostics.ProcessStartInfo startInfo = GameLauncher.CreateStartInfo(game, session);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(gameRoot, startInfo.WorkingDirectory);
        Assert.Equal(GameInstallValidator.ExpectedSteamAppId, startInfo.Environment["SteamAppId"]);
        Assert.Equal(GameInstallValidator.ExpectedSteamAppId, startInfo.Environment["SteamGameId"]);
        Assert.Equal("1", startInfo.Environment[Protocol.EnabledEnvironmentVariable]);
        Assert.Equal("test-token", startInfo.Environment[Protocol.TokenEnvironmentVariable]);
        Assert.Equal("1", startInfo.Environment[Protocol.CheatModeAllowedEnvironmentVariable]);
        Assert.Equal(new[] { "-logFile", session.LogPath }, startInfo.ArgumentList);
    }

    [Fact]
    public void BuildProfileRoot_AlwaysUsesNamedGameScopedQaProfile()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "LoopstructorProfileTest");

        string first = ActivationSessionFactory.BuildProfileRoot(
            dataRoot, "game-123", "qa-default");
        string second = ActivationSessionFactory.BuildProfileRoot(
            dataRoot, "game-123", "qa-default");

        string expected = Path.Combine(dataRoot, "profiles", "game-123", "qa-default");
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "cheat" + Path.DirectorySeparatorChar, first, StringComparison.Ordinal);
    }
}
