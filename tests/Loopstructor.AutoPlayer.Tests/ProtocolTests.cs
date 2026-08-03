using Loopstructor.AutoPlayer.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void BridgeHello_GameProcessIdIsBackwardCompatibleWithoutProtocolChange()
    {
        BridgeHello legacy = JsonConvert.DeserializeObject<BridgeHello>("{\"ProtocolVersion\":1,\"PluginVersion\":\"0.1.0\"}")!;
        string current = JsonConvert.SerializeObject(new BridgeHello
        {
            ProtocolVersion = Protocol.CurrentVersion,
            GameProcessId = 4321
        });

        Assert.Equal(1, Protocol.CurrentVersion);
        Assert.Equal(0, legacy.GameProcessId);
        Assert.Contains("\"GameProcessId\":4321", current, StringComparison.Ordinal);
    }

    [Fact]
    public void CheatFields_AreOptionalForLegacyProtocolAndRoundTripStructuredData()
    {
        ControlRequest legacy = JsonConvert.DeserializeObject<ControlRequest>("{\"command\":\"status\"}")!;
        ControlRequest current = new()
        {
            Command = CheatCommands.SpawnEnemy,
            Arguments = new JObject
            {
                ["enemyId"] = "CommonMonster",
                ["x"] = 12.5,
                ["y"] = -3
            }
        };

        string json = JsonConvert.SerializeObject(current);
        ControlRequest roundTrip = JsonConvert.DeserializeObject<ControlRequest>(json)!;

        Assert.Null(legacy.Arguments);
        Assert.False(JsonConvert.DeserializeObject<BridgeHello>("{}")!.CheatSessionAuthorized);
        Assert.Equal(1, Protocol.CheatCurrentVersion);
        Assert.True(CheatCommands.IsCheatCommand(roundTrip.Command));
        Assert.Equal("CommonMonster", roundTrip.Arguments!.Value<string>("enemyId"));
        Assert.Equal(12.5, roundTrip.Arguments.Value<double>("x"));
    }

    [Fact]
    public void CheatSessionStatus_RoundTripsExplicitIntegrityState()
    {
        AutoPlayerStatus status = new()
        {
            CheatSessionAuthorized = true,
            CheatAvailable = true,
            RunIntegrity = "cheat-session",
            BaseGodModeEnabled = true
        };

        AutoPlayerStatus roundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(
            JsonConvert.SerializeObject(status))!;

        Assert.True(roundTrip.CheatSessionAuthorized);
        Assert.True(roundTrip.CheatAvailable);
        Assert.True(roundTrip.BaseGodModeEnabled);
        Assert.Equal("cheat-session", roundTrip.RunIntegrity);
    }

    [Fact]
    public void CheatCommands_ExposeOnlyNamespacedFixedOperations()
    {
        Assert.Equal(15, CheatCommands.All.Count);
        Assert.Equal(10, CheatCommands.Mutations.Count);
        Assert.All(CheatCommands.All, command => Assert.StartsWith("cheat.", command, StringComparison.Ordinal));
        Assert.DoesNotContain(CheatCommands.All, command => command.Contains("reflect", StringComparison.OrdinalIgnoreCase));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SpawnEnemy));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantVehicle.ToUpperInvariant()));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.QueryEnemies));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetEnabled));
    }

    [Fact]
    public void HashGameRoot_IsStableAcrossCaseAndTrailingSeparators()
    {
        string root = Path.Combine(Path.GetTempPath(), "LoopstructorHashTest", "Game");
        string variant = root.ToLowerInvariant() + Path.DirectorySeparatorChar;

        string first = Protocol.HashGameRoot(root);
        string second = Protocol.HashGameRoot(variant);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void HashGameRoot_DiffersForDifferentInstallations()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "LoopstructorHashTest");

        string first = Protocol.HashGameRoot(Path.Combine(basePath, "GameA"));
        string second = Protocol.HashGameRoot(Path.Combine(basePath, "GameB"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetTicketPath_IsStableAndOwnedByApplicationTicketDirectory()
    {
        string gameRoot = Path.Combine(Path.GetTempPath(), "LoopstructorTicketTest", "Game");
        string expectedDirectory = Path.GetFullPath(Path.Combine(Protocol.DataRoot, "tickets"));
        string expectedPrefix = Protocol.HashGameRoot(gameRoot).Substring(0, 16);

        string first = Protocol.GetTicketPath(gameRoot);
        string second = Protocol.GetTicketPath(gameRoot + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.Equal(expectedDirectory, Path.GetFullPath(Path.GetDirectoryName(first)!));
        Assert.Equal("launch-" + expectedPrefix + ".json", Path.GetFileName(first));
    }
}
