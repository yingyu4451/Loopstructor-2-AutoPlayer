using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class AutomationRunOptions
{
    public AutomationGameMode Mode { get; set; } = AutomationGameMode.Common;
    public int CharacterIndex { get; set; }
    public int DifficultyIndex { get; set; }
    public int SuperModuleIndex { get; set; }
    public int RandomVehicleIndex { get; set; }
    public int RandomFetterIndex { get; set; }
    public int SpeedState { get; set; } = 2;
    public int MaxRunMinutes { get; set; } = 120;
    public int MaxWaves { get; set; }
    public bool ContinueExistingProfile { get; set; }
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
    public AutoPlayerRunState RunState { get; set; } = AutoPlayerRunState.Standby;
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
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActionAtUtc { get; set; }
    public string LastCommand { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;
    public IReadOnlyList<TimelineEvent> Timeline { get; set; } = Array.Empty<TimelineEvent>();
}

public sealed class BridgeHello
{
    public int ProtocolVersion { get; set; }
    public int GameProcessId { get; set; }
    public string PluginVersion { get; set; } = string.Empty;
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
}

public sealed class ControlRequest
{
    public string Id { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Command { get; set; } = "status";
    public AutomationRunOptions? Options { get; set; }
}

public sealed class ControlResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public AutoPlayerStatus? Status { get; set; }
    public BridgeHello? Hello { get; set; }
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
}

public static class Protocol
{
    public const int CurrentVersion = 1;
    public const string EnabledEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ENABLED";
    public const string TokenEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_TOKEN";
    public const string PipeEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_PIPE";
    public const string ProfileEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_PROFILE_ROOT";
    public const string ArtifactEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ARTIFACT_ROOT";
    public const string ExpectedAssemblySha256EnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_ASSEMBLY_SHA256";

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
}
