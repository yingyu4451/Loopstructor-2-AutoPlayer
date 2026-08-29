using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void BridgeHello_GameProcessIdRoundTripsWithCurrentProtocol()
    {
        BridgeHello legacy = JsonConvert.DeserializeObject<BridgeHello>("{\"ProtocolVersion\":1,\"PluginVersion\":\"0.1.0\"}")!;
        string current = JsonConvert.SerializeObject(new BridgeHello
        {
            ProtocolVersion = Protocol.CurrentVersion,
            GameProcessId = 4321,
            ProcessInstanceId = "0123456789abcdef0123456789abcdef"
        });

        Assert.Equal(3, Protocol.CurrentVersion);
        Assert.Equal(0, legacy.GameProcessId);
        Assert.Empty(legacy.ProcessInstanceId);
        Assert.Contains("\"GameProcessId\":4321", current, StringComparison.Ordinal);
        Assert.Contains("\"ProcessInstanceId\"", current, StringComparison.Ordinal);
    }

    [Fact]
    public void CheatFields_AreOptionalForLegacyProtocolAndRoundTripStructuredData()
    {
        ControlRequest legacy = JsonConvert.DeserializeObject<ControlRequest>("{\"command\":\"status\"}")!;
        ControlRequest current = new()
        {
            TargetGameProcessId = 4321,
            TargetProcessInstanceId = "0123456789abcdef0123456789abcdef",
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
        Assert.Equal(7, Protocol.CheatCurrentVersion);
        Assert.Equal(4321, roundTrip.TargetGameProcessId);
        Assert.Equal(current.TargetProcessInstanceId, roundTrip.TargetProcessInstanceId);
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
            EnemyBuffsVisible = true,
            BaseGodModeEnabled = true,
            MapSkipEnabled = true
        };

        AutoPlayerStatus roundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(
            JsonConvert.SerializeObject(status))!;

        Assert.True(roundTrip.CheatSessionAuthorized);
        Assert.True(roundTrip.CheatAvailable);
        Assert.True(roundTrip.EnemyBuffsVisible);
        Assert.True(roundTrip.BaseGodModeEnabled);
        Assert.True(roundTrip.MapSkipEnabled);
        Assert.Equal("cheat-modified", roundTrip.RunIntegrity);
    }

    [Fact]
    public void AutoPlayerStatus_RoundTripsChapterAndRuntimeTimingTelemetry()
    {
        AutoPlayerStatus status = new()
        {
            CurrentMapStage = 2,
            CurrentMapLayer = 41,
            LastRuntimeCommand = "queryWave",
            LastRuntimeCommandDurationMs = 4.25,
            MaxRuntimeCommand = "queryDisposableGridOptions",
            MaxRuntimeCommandDurationMs = 38.5,
            SlowRuntimeCommandCount = 2,
            CurrentFps = 58.75,
            OnePercentLowFps = 31.5,
            FrameTimeP99Ms = 31.75,
            FrameSampleCount = 587,
            FrameTelemetryWindowSeconds = 10.01
        };

        AutoPlayerStatus roundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(
            JsonConvert.SerializeObject(status))!;

        Assert.Equal(3, roundTrip.CurrentChapter);
        Assert.Equal(2, roundTrip.CurrentMapStage);
        Assert.Equal(41, roundTrip.CurrentMapLayer);
        Assert.Equal("queryWave", roundTrip.LastRuntimeCommand);
        Assert.Equal(4.25, roundTrip.LastRuntimeCommandDurationMs);
        Assert.Equal("queryDisposableGridOptions", roundTrip.MaxRuntimeCommand);
        Assert.Equal(38.5, roundTrip.MaxRuntimeCommandDurationMs);
        Assert.Equal(2, roundTrip.SlowRuntimeCommandCount);
        Assert.Equal(58.75, roundTrip.CurrentFps);
        Assert.Equal(31.5, roundTrip.OnePercentLowFps);
        Assert.Equal(31.75, roundTrip.FrameTimeP99Ms);
        Assert.Equal(587, roundTrip.FrameSampleCount);
        Assert.Equal(10.01, roundTrip.FrameTelemetryWindowSeconds);
    }

    [Fact]
    public void AutomationRunOptions_RoundTripStoryAndDecisionPriority()
    {
        AutomationRunOptions legacy = JsonConvert.DeserializeObject<AutomationRunOptions>("{}")!;
        AutomationRunOptions current = new()
        {
            SkipStory = true,
            DecisionPriority = AutomationDecisionPriority.VehicleRewards
        };

        AutomationRunOptions roundTrip = JsonConvert.DeserializeObject<AutomationRunOptions>(
            JsonConvert.SerializeObject(current))!;

        Assert.False(legacy.SkipStory);
        Assert.Equal(AutomationDecisionPriority.CatapultPoints, legacy.DecisionPriority);
        Assert.True(roundTrip.SkipStory);
        Assert.Equal(AutomationDecisionPriority.VehicleRewards, roundTrip.DecisionPriority);
    }

    [Fact]
    public void CheatCommands_ExposeOnlyNamespacedFixedOperations()
    {
        Assert.Equal(33, CheatCommands.All.Count);
        Assert.Equal(23, CheatCommands.Mutations.Count);
        Assert.Equal(7, CheatCommands.AutoPlayObservationCommands.Count);
        Assert.All(CheatCommands.All, command => Assert.StartsWith("cheat.", command, StringComparison.Ordinal));
        Assert.DoesNotContain(CheatCommands.All, command => command.Contains("reflect", StringComparison.OrdinalIgnoreCase));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SpawnEnemy));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SetVehicleEnchantment));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SetMapSkipEnabled));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetEnemyIdOverlay));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetEnemyBuffOverlay));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveVehicle));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveRelic));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantAllRelics));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveFieldCatapultPoint));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.ClearFieldCatapultPoints));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.ClearConsumables));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.ClearBackpackCatapultPoints));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.RemoveAllRelics));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SetFieldCatapultDeleteMode));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.SkipRewardPopup));
        Assert.True(CheatCommands.IsMutationCommand(CheatCommands.GrantVehicle.ToUpperInvariant()));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.QueryEnemies));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetEnabled));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.SetSpawnPointCapture));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.RemoveSpawnPoint));
        Assert.False(CheatCommands.IsMutationCommand(CheatCommands.ClearSpawnPoints));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.SetEnabled));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.QueryCatalog));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.QueryVehicles));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.QueryEnemies));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.SetEnemyIdOverlay));
        Assert.True(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.SetEnemyBuffOverlay.ToUpperInvariant()));
        Assert.False(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.ClearEnemies));
        Assert.False(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.SetBaseGodMode));
        Assert.False(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.GrantAllRelics));
        Assert.False(CheatCommands.IsAutoPlayObservationCommand(CheatCommands.SkipRewardPopup));
    }

    [Fact]
    public void ActivationMode_RoundTripsWithCurrentBaseProtocolVersion()
    {
        BridgeHello hello = new() { ActivationMode = AutoPlayerActivationMode.ResidentPlayer };
        AutoPlayerStatus status = new() { ActivationMode = AutoPlayerActivationMode.ResidentPlayer };

        BridgeHello helloRoundTrip = JsonConvert.DeserializeObject<BridgeHello>(JsonConvert.SerializeObject(hello))!;
        AutoPlayerStatus statusRoundTrip = JsonConvert.DeserializeObject<AutoPlayerStatus>(JsonConvert.SerializeObject(status))!;

        Assert.Equal(3, Protocol.CurrentVersion);
        Assert.Equal(AutoPlayerActivationMode.ResidentPlayer, helloRoundTrip.ActivationMode);
        Assert.Equal(AutoPlayerActivationMode.ResidentPlayer, statusRoundTrip.ActivationMode);
    }

    [Fact]
    public void ControlPipeName_IsProcessScopedOnlyForResidentPlayerMode()
    {
        const string basePipeName = "Loopstructor.AutoPlayer.Player.test";

        Assert.Equal(
            basePipeName + ".pid-4321",
            Protocol.GetControlPipeName(basePipeName, AutoPlayerActivationMode.ResidentPlayer, 4321));
        Assert.Equal(
            basePipeName,
            Protocol.GetControlPipeName(basePipeName, AutoPlayerActivationMode.IsolatedQa, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Protocol.GetControlPipeName(basePipeName, AutoPlayerActivationMode.ResidentPlayer, 0));
        Assert.Throws<ArgumentException>(() =>
            Protocol.GetControlPipeName("invalid/pipe", AutoPlayerActivationMode.IsolatedQa, 0));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("", false)]
    [InlineData("not-a-request-id", false)]
    public void RequestId_RequiresCompactGuid(string value, bool expected)
    {
        Assert.Equal(expected, Protocol.IsValidRequestId(value));
    }

    [Fact]
    public void CheatWindowSessionKey_ChangesWhenPidIsReusedByNewPluginInstance()
    {
        BridgeHello first = new()
        {
            GameProcessId = 4321,
            ProcessInstanceId = "0123456789abcdef0123456789abcdef",
            BuildGuid = "build",
            AssemblySha256 = new string('a', 64),
            ArtifactRoot = "artifact"
        };
        BridgeHello second = JsonConvert.DeserializeObject<BridgeHello>(JsonConvert.SerializeObject(first))!;
        second.ProcessInstanceId = "fedcba9876543210fedcba9876543210";

        Assert.NotEqual(CheatForm.BuildSessionKey(first), CheatForm.BuildSessionKey(second));
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
