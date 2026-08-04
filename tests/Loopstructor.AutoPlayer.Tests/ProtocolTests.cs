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
        Assert.Equal(4, Protocol.CheatCurrentVersion);
        Assert.True(CheatCommands.IsCheatCommand(roundTrip.Command));
        Assert.Equal("CommonMonster", roundTrip.Arguments!.Value<string>("enemyId"));
        Assert.Equal(12.5, roundTrip.Arguments.Value<double>("x"));
    }

    [Fact]
    public void CheatCapabilityStatus_RoundTripsExplicitIntegrityState()
    {
        AutoPlayerStatus status = new()
        {
            CheatSessionAuthorized = true,
            CheatAvailable = true,
            RunIntegrity = "cheat-modified",
            BaseGodModeEnabled = true,
            MapSkipEnabled = true
        };

        AutoPlayerStatus roundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(
            JsonConvert.SerializeObject(status))!;

        Assert.True(roundTrip.CheatSessionAuthorized);
        Assert.True(roundTrip.CheatAvailable);
        Assert.True(roundTrip.BaseGodModeEnabled);
        Assert.True(roundTrip.MapSkipEnabled);
        Assert.Equal("cheat-modified", roundTrip.RunIntegrity);
    }

    [Fact]
    public void CheatCommands_ExposeOnlyNamespacedFixedOperations()
    {
        Assert.Equal(26, CheatCommands.All.Count);
        Assert.Equal(18, CheatCommands.Mutations.Count);
        Assert.All(CheatCommands.All, command => Assert.StartsWith("cheat.", command, StringComparison.Ordinal));
        Assert.DoesNotContain(CheatCommands.All, command => command.Contains("reflect", StringComparison.OrdinalIgnoreCase));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SpawnEnemy));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SetVehicleEnchantment));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SetMapSkipEnabled));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveVehicle));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveRelic));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveFieldCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.ClearFieldCatapultPoints));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantVehicle.ToUpperInvariant()));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.QueryEnemies));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetEnabled));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetSpawnPointCapture));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.RemoveSpawnPoint));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.ClearSpawnPoints));
    }

    [Fact]
    public void ActivationMode_RoundTripsWithoutChangingBaseProtocolVersion()
    {
        BridgeHello hello = new() { ActivationMode = AutoPlayerActivationMode.ResidentPlayer };
        AutoPlayerStatus status = new() { ActivationMode = AutoPlayerActivationMode.ResidentPlayer };

        BridgeHello helloRoundTrip = JsonConvert.DeserializeObject<BridgeHello>(JsonConvert.SerializeObject(hello))!;
        AutoPlayerStatus statusRoundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(JsonConvert.SerializeObject(status))!;

        Assert.Equal(1, Protocol.CurrentVersion);
        Assert.Equal(AutoPlayerActivationMode.ResidentPlayer, helloRoundTrip.ActivationMode);
        Assert.Equal(AutoPlayerActivationMode.ResidentPlayer, statusRoundTrip.ActivationMode);
    }

    [Theory]
    [InlineData(AutoPlayerActivationMode.IsolatedQa, true, true, true, true, true)]
    [InlineData(AutoPlayerActivationMode.IsolatedQa, true, false, true, true, false)]
    [InlineData(AutoPlayerActivationMode.ResidentPlayer, false, false, false, false, true)]
    [InlineData(AutoPlayerActivationMode.ResidentPlayer, true, true, true, true, false)]
    [InlineData(AutoPlayerActivationMode.ResidentPlayer, false, true, false, false, false)]
    public void SafetyGate_UsesModeSpecificIsolationRequirements(
        AutoPlayerActivationMode mode,
        bool saveIsolationApplied,
        bool saveIsolationVerified,
        bool platformWritesBlocked,
        bool gameArtifactsRedirected,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoPlayerSafetyGate.IsReady(
                mode,
                saveIsolationApplied,
                saveIsolationVerified,
                platformWritesBlocked,
                gameArtifactsRedirected));
    }

    [Fact]
    public void SpawnEnemyProtocol_SupportsMultiplePointsAndExplicitLevelSource()
    {
        ControlRequest request = new()
        {
            Command = CheatCommands.SpawnEnemy,
            Arguments = new JObject
            {
                ["enemyId"] = "CommonMonster",
                ["count"] = 2,
                ["levelMode"] = "current",
                ["points"] = new JArray(
                    new JObject { ["pointId"] = "a", ["x"] = 1, ["y"] = 2, ["z"] = 0 },
                    new JObject { ["pointId"] = "b", ["x"] = 3, ["y"] = 4, ["z"] = 0 })
            }
        };

        ControlRequest roundTrip = JsonConvert.DeserializeObject<ControlRequest>(JsonConvert.SerializeObject(request))!;

        Assert.Equal("current", roundTrip.Arguments!.Value<string>("levelMode"));
        Assert.Equal(2, ((JArray)roundTrip.Arguments["points"]!).Count);
        Assert.Null(roundTrip.Arguments["level"]);
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
