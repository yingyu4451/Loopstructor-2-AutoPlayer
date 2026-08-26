using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Loopstructor.AutoPlayer.Core;

public enum AutoPlayerRunState
{
    Standby,
    Running,
    Paused,
    Completed,
    Faulted,
    Incompatible
}

public enum AutomationOutcome
{
    Unknown,
    InProgress,
    Victory,
    Defeat,
    Timeout,
    WaveLimit,
    Stopped,
    Error
}

public enum AutomationStage
{
    WaitingForGame,
    FrontEnd,
    RandomSelection,
    InitializingRun,
    PreparingDefense,
    ManagingRewards,
    ManagingEvent,
    ManagingShop,
    SelectingRoute,
    StartingWave,
    Battle,
    Completed,
    Recovery
}

public enum AutomationGameMode
{
    Common,
    Random
}

public enum AutomationDecisionPriority
{
    ThreeStarVehicles = 0,
    CatapultPoints = 1,
    Relics = 2
}

public enum AutoPlayerActivationMode
{
    IsolatedQa,
    ResidentPlayer
}

public static class AutoPlayerSafetyGate
{
    public static bool IsReady(
        AutoPlayerActivationMode mode,
        bool saveIsolationApplied,
        bool saveIsolationVerified,
        bool platformWritesBlocked,
        bool gameArtifactsRedirected) => mode switch
    {
        AutoPlayerActivationMode.IsolatedQa =>
            saveIsolationApplied
            && saveIsolationVerified
            && platformWritesBlocked
            && gameArtifactsRedirected,
        AutoPlayerActivationMode.ResidentPlayer =>
            !saveIsolationApplied
            && !saveIsolationVerified
            && !platformWritesBlocked
            && !gameArtifactsRedirected,
        _ => false
    };
}

public sealed class AutomationRunOptions
{
    public AutomationGameMode Mode { get; set; } = AutomationGameMode.Common;
    public int CharacterIndex { get; set; }
    public int DifficultyIndex { get; set; }
    public int SuperModuleIndex { get; set; }
    public int RandomVehicleIndex { get; set; }
    public int RandomFetterIndex { get; set; }
    public int GameSpeedControlVersion { get; set; }
    public bool OverrideGameSpeed { get; set; } = true;
    public int SpeedState { get; set; }
    public int MaxRunMinutes { get; set; } = 120;
    public int MaxWaves { get; set; }
    public bool ContinueExistingProfile { get; set; }
    public bool SkipStory { get; set; }
    public AutomationDecisionPriority DecisionPriority { get; set; } = AutomationDecisionPriority.CatapultPoints;
}

public sealed class AutomationAction
{
    public AutomationAction(string command, JObject? arguments, AutomationStage stage, string reason)
    {
        Command = command;
        Arguments = arguments ?? new JObject();
        Stage = stage;
        Reason = reason;
    }

    public string Command { get; }
    public JObject Arguments { get; }
    public AutomationStage Stage { get; }
    public string Reason { get; }

    public static AutomationAction Wait(AutomationStage stage, string reason) =>
        new("wait", new JObject(), stage, reason);
}

public sealed class TimelineEvent
{
    public DateTime TimestampUtc { get; set; }
    public AutomationStage Stage { get; set; }
    public string Kind { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}

public sealed class AutoPlayerStatus
{
    public int ProtocolVersion { get; set; } = Protocol.CurrentVersion;
    public string PluginVersion { get; set; } = string.Empty;
    public AutoPlayerActivationMode ActivationMode { get; set; } = AutoPlayerActivationMode.IsolatedQa;
    public AutoPlayerRunState RunState { get; set; } = AutoPlayerRunState.Standby;
    public AutomationOutcome Outcome { get; set; } = AutomationOutcome.Unknown;
    public AutomationStage Stage { get; set; } = AutomationStage.WaitingForGame;
    public string StageDetail { get; set; } = string.Empty;
    public string Scene { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public string BuildGuid { get; set; } = string.Empty;
    public string SteamBuildId { get; set; } = string.Empty;
    public string AssemblySha256 { get; set; } = string.Empty;
    public string AssemblyMvid { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> ManagedAssemblySha256 { get; set; } =
        new Dictionary<string, string>();
    public bool ProductIdentityValid { get; set; }
    public bool FingerprintAccepted { get; set; }
    public string CompatibilityError { get; set; } = string.Empty;
    public bool RuntimeContractAvailable { get; set; }
    public IReadOnlyList<string> MissingRuntimeMembers { get; set; } = Array.Empty<string>();
    public bool SaveIsolationApplied { get; set; }
    public bool SaveIsolationVerified { get; set; }
    public string SaveIsolationError { get; set; } = string.Empty;
    public bool PlatformWritesBlocked { get; set; }
    public bool GameArtifactsRedirected { get; set; }
    public string IsolatedSaveRoot { get; set; } = string.Empty;
    public string ArtifactDirectory { get; set; } = string.Empty;
    public bool NeedsProcessRestart { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int WavesStarted { get; set; }
    public int WavesCompleted { get; set; }
    public int CurrentMapStage { get; set; } = -1;
    public int CurrentMapLayer { get; set; } = -1;
    public int CurrentChapter => CurrentMapStage >= 0 ? CurrentMapStage + 1 : 0;
    public string LastRuntimeCommand { get; set; } = string.Empty;
    public double LastRuntimeCommandDurationMs { get; set; }
    public string MaxRuntimeCommand { get; set; } = string.Empty;
    public double MaxRuntimeCommandDurationMs { get; set; }
    public int SlowRuntimeCommandCount { get; set; }
    public double CurrentFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double FrameTimeP99Ms { get; set; }
    public int FrameSampleCount { get; set; }
    public double FrameTelemetryWindowSeconds { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActionAtUtc { get; set; }
    public string LastCommand { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;
    public bool CheatSessionAuthorized { get; set; }
    public bool CheatAvailable { get; set; }
    public bool CheatModeEnabled { get; set; }
    public bool CheatUsed { get; set; }
    public int CheatActionCount { get; set; }
    public bool EnemyIdsVisible { get; set; }
    public bool EnemyBuffsVisible { get; set; }
    public bool BaseGodModeEnabled { get; set; }
    public bool MapSkipEnabled { get; set; }
    public string RunIntegrity { get; set; } = "clean";
    public string CheatAvailabilityReason { get; set; } = string.Empty;
    public IReadOnlyList<TimelineEvent> Timeline { get; set; } = Array.Empty<TimelineEvent>();
}

public sealed class BridgeHello
{
    public int ProtocolVersion { get; set; }
    public int GameProcessId { get; set; }
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public AutoPlayerActivationMode ActivationMode { get; set; } = AutoPlayerActivationMode.IsolatedQa;
    public string GameVersion { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public string BuildGuid { get; set; } = string.Empty;
    public string AssemblySha256 { get; set; } = string.Empty;
    public string AssemblyMvid { get; set; } = string.Empty;
    public bool ProductIdentityValid { get; set; }
    public bool FingerprintAccepted { get; set; }
    public string CompatibilityError { get; set; } = string.Empty;
    public bool RuntimeContractAvailable { get; set; }
    public IReadOnlyList<string> MissingMembers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Commands { get; set; } = Array.Empty<string>();
    public bool SaveIsolationApplied { get; set; }
    public bool SaveIsolationVerified { get; set; }
    public bool PlatformWritesBlocked { get; set; }
    public bool GameArtifactsRedirected { get; set; }
    public string ProfileRoot { get; set; } = string.Empty;
    public string ArtifactRoot { get; set; } = string.Empty;
    public int CheatProtocolVersion { get; set; }
    public bool CheatSessionAuthorized { get; set; }
    public bool CheatAvailable { get; set; }
    public bool CheatModeEnabled { get; set; }
    public bool CheatUsed { get; set; }
    public bool MapSkipEnabled { get; set; }
    public string CheatAvailabilityReason { get; set; } = string.Empty;
    public IReadOnlyList<string> CheatCapabilities { get; set; } = Array.Empty<string>();
}

public sealed class ControlRequest
{
    public string Id { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public int TargetGameProcessId { get; set; }
    public string TargetProcessInstanceId { get; set; } = string.Empty;
    public string Command { get; set; } = "status";
    public AutomationRunOptions? Options { get; set; }
    public JObject? Arguments { get; set; }
}

public static class AutoPlayerGameSpeed
{
    public const int CurrentOptionsVersion = 1;

    public static void Normalize(AutomationRunOptions options)
    {
        if (options.GameSpeedControlVersion < CurrentOptionsVersion)
        {
            // Older Managers always sent their historical 3x default. Reset that persisted game setting once.
            options.OverrideGameSpeed = true;
            options.SpeedState = 0;
        }

        options.GameSpeedControlVersion = CurrentOptionsVersion;
        options.SpeedState = Math.Max(0, Math.Min(2, options.SpeedState));
    }
}

public sealed class ControlResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public AutoPlayerStatus? Status { get; set; }
    public BridgeHello? Hello { get; set; }
    public JObject? Data { get; set; }
}

public sealed class LaunchTicket
{
    public int Protocol { get; set; } = Loopstructor.AutoPlayer.Core.Protocol.CurrentVersion;
    public DateTime ExpiresUtc { get; set; }
    public string GameRootSha256 { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string ProfileRoot { get; set; } = string.Empty;
    public string ArtifactRoot { get; set; } = string.Empty;
    public string ExpectedAssemblySha256 { get; set; } = string.Empty;
    public bool CheatModeAllowed { get; set; }
}

public static class Protocol
{
    public const int CurrentVersion = 3;
    public const int CheatCurrentVersion = 7;
    public const string EnabledEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ENABLED";
    public const string TokenEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_TOKEN";
    public const string PipeEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_PIPE";
    public const string ProfileEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_PROFILE_ROOT";
    public const string ArtifactEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ARTIFACT_ROOT";
    public const string ExpectedAssemblySha256EnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ASSEMBLY_SHA256";
    public const string CheatModeAllowedEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_CHEAT_ALLOWED";

    public static bool IsValidRequestId(string? requestId) =>
        Guid.TryParseExact(requestId, "N", out _);

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoopstructorAutoPlayer");

    public static string HashGameRoot(string gameRoot)
    {
        string normalized = Path.GetFullPath(gameRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        StringBuilder result = new(digest.Length * 2);
        foreach (byte value in digest)
        {
            result.Append(value.ToString("x2"));
        }

        return result.ToString();
    }

    public static string GetTicketPath(string gameRoot)
    {
        string id = HashGameRoot(gameRoot).Substring(0, 16);
        return Path.Combine(DataRoot, "tickets", "launch-" + id + ".json");
    }

    public static string GetControlPipeName(
        string basePipeName,
        AutoPlayerActivationMode activationMode,
        int gameProcessId)
    {
        if (string.IsNullOrWhiteSpace(basePipeName)
            || basePipeName.Length > 220
            || basePipeName.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0)
        {
            throw new ArgumentException("控制管道基础名称无效。", nameof(basePipeName));
        }

        if (activationMode != AutoPlayerActivationMode.ResidentPlayer)
        {
            return basePipeName;
        }

        if (gameProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameProcessId), "玩家模式需要有效的游戏进程 PID。");
        }

        string processPipeName = basePipeName + ".pid-" + gameProcessId;
        if (processPipeName.Length > 240)
        {
            throw new ArgumentException("进程专属控制管道名称过长。", nameof(basePipeName));
        }

        return processPipeName;
    }
}

public static class CheatCommands
{
    public const string SetEnabled = "cheat.setEnabled";
    public const string QueryCatalog = "cheat.queryCatalog";
    public const string QueryState = "cheat.queryState";
    public const string GrantVehicle = "cheat.grantVehicle";
    public const string RemoveVehicle = "cheat.removeVehicle";
    public const string GrantDisposable = "cheat.grantDisposable";
    public const string ClearConsumables = "cheat.clearConsumables";
    public const string GrantCatapultPoint = "cheat.grantCatapultPoint";
    public const string RemoveCatapultPoint = "cheat.removeCatapultPoint";
    public const string ClearBackpackCatapultPoints = "cheat.clearBackpackCatapultPoints";
    public const string RemoveFieldCatapultPoint = "cheat.removeFieldCatapultPoint";
    public const string ClearFieldCatapultPoints = "cheat.clearFieldCatapultPoints";
    public const string SetFieldCatapultDeleteMode = "cheat.setFieldCatapultDeleteMode";
    public const string SetBaseGodMode = "cheat.setBaseGodMode";
    public const string EndWave = "cheat.endWave";
    public const string ClearEnemies = "cheat.clearEnemies";
    public const string QueryVehicles = "cheat.queryVehicles";
    public const string ModifyVehicle = "cheat.modifyVehicle";
    public const string SetVehicleEnchantment = "cheat.setVehicleEnchantment";
    public const string QueryEnemies = "cheat.queryEnemies";
    public const string ModifyEnemy = "cheat.modifyEnemy";
    public const string SetEnemyIdOverlay = "cheat.setEnemyIdOverlay";
    public const string SetEnemyBuffOverlay = "cheat.setEnemyBuffOverlay";
    public const string GrantRelic = "cheat.grantRelic";
    public const string GrantAllRelics = "cheat.grantAllRelics";
    public const string RemoveRelic = "cheat.removeRelic";
    public const string RemoveAllRelics = "cheat.removeAllRelics";
    public const string SetSpawnPointCapture = "cheat.setSpawnPointCapture";
    public const string RemoveSpawnPoint = "cheat.removeSpawnPoint";
    public const string ClearSpawnPoints = "cheat.clearSpawnPoints";
    public const string SpawnEnemy = "cheat.spawnEnemy";
    public const string SetMapSkipEnabled = "cheat.setMapSkipEnabled";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        SetEnabled,
        QueryCatalog,
        QueryState,
        GrantVehicle,
        RemoveVehicle,
        GrantDisposable,
        ClearConsumables,
        GrantCatapultPoint,
        RemoveCatapultPoint,
        ClearBackpackCatapultPoints,
        RemoveFieldCatapultPoint,
        ClearFieldCatapultPoints,
        SetFieldCatapultDeleteMode,
        SetBaseGodMode,
        EndWave,
        ClearEnemies,
        QueryVehicles,
        ModifyVehicle,
        SetVehicleEnchantment,
        QueryEnemies,
        ModifyEnemy,
        SetEnemyIdOverlay,
        SetEnemyBuffOverlay,
        GrantRelic,
        GrantAllRelics,
        RemoveRelic,
        RemoveAllRelics,
        SetSpawnPointCapture,
        RemoveSpawnPoint,
        ClearSpawnPoints,
        SpawnEnemy,
        SetMapSkipEnabled
    };

    public static IReadOnlyList<string> Mutations { get; } = new[]
    {
        GrantVehicle,
        RemoveVehicle,
        GrantDisposable,
        ClearConsumables,
        GrantCatapultPoint,
        RemoveCatapultPoint,
        ClearBackpackCatapultPoints,
        RemoveFieldCatapultPoint,
        ClearFieldCatapultPoints,
        SetFieldCatapultDeleteMode,
        SetBaseGodMode,
        EndWave,
        ClearEnemies,
        ModifyVehicle,
        SetVehicleEnchantment,
        ModifyEnemy,
        GrantRelic,
        GrantAllRelics,
        RemoveRelic,
        RemoveAllRelics,
        SpawnEnemy,
        SetMapSkipEnabled
    };

    public static IReadOnlyList<string> AutoPlayObservationCommands { get; } = new[]
    {
        SetEnabled,
        QueryCatalog,
        QueryState,
        QueryVehicles,
        QueryEnemies,
        SetEnemyIdOverlay,
        SetEnemyBuffOverlay
    };

    public static bool IsCheatCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) &&
        (command ?? string.Empty).StartsWith("cheat.", StringComparison.OrdinalIgnoreCase);

    public static bool IsMutationCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) &&
        Mutations.Contains(command ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static bool IsAutoPlayObservationCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) &&
        AutoPlayObservationCommands.Contains(command ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}
