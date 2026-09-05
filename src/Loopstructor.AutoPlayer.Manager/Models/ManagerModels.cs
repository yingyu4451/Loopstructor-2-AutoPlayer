using Loopstructor.AutoPlayer.Core;
using System.Drawing;

namespace Loopstructor.AutoPlayer.Manager.Models;

public sealed class ManagerLaunchOptions
{
    public bool RestartedAfterUpdate { get; private set; }
    public bool DemoMode { get; private set; }
    public bool DemoRestartRequired { get; private set; }
    public bool DemoCheatWindow { get; private set; }
    public int DemoCheatTab { get; private set; }
    public bool ScreenshotMode { get; private set; }
    public bool ExitAfterScreenshot { get; private set; }
    public string ScreenshotOutput { get; private set; } = string.Empty;
    public Size? WindowSize { get; private set; }

    public static ManagerLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ManagerLaunchOptions result = new();
        string[] args = arguments.ToArray();
        for (int index = 0; index < args.Length; index++)
        {
            string current = args[index];
            switch (current.ToLowerInvariant())
            {
                case "--restarted-after-update":
                    result.RestartedAfterUpdate = true;
                    break;
                case "--demo":
                    result.DemoMode = true;
                    break;
                case "--demo-restart-required":
                    result.DemoMode = true;
                    result.DemoRestartRequired = true;
                    break;
                case "--demo-cheat-window":
                    result.DemoMode = true;
                    result.DemoCheatWindow = true;
                    break;
                case "--demo-cheat-tab" when index + 1 < args.Length:
                    if (int.TryParse(args[++index], out int tabIndex) && tabIndex is >= 0 and <= 5)
                    {
                        result.DemoCheatTab = tabIndex;
                    }

                    break;
                case "--screenshot-mode":
                    result.DemoMode = true;
                    result.ScreenshotMode = true;
                    break;
                case "--exit-after-screenshot":
                    result.ExitAfterScreenshot = true;
                    break;
                case "--screenshot-output" when index + 1 < args.Length:
                    result.ScreenshotOutput = Path.GetFullPath(args[++index]);
                    break;
                case "--window-size" when index + 1 < args.Length:
                    string[] dimensions = args[++index].Split('x', 'X');
                    if (dimensions.Length == 2
                        && int.TryParse(dimensions[0], out int width)
                        && int.TryParse(dimensions[1], out int height)
                        && width is >= 980 and <= 2560
                        && height is >= 680 and <= 1600)
                    {
                        result.WindowSize = new Size(width, height);
                    }

                    break;
            }
        }

        return result;
    }
}

internal readonly record struct RunControlAvailability(
    bool CanStart,
    bool CanPause,
    bool CanResume,
    bool CanStop)
{
    public static RunControlAvailability From(bool sessionTrusted, AutoPlayerStatus? status)
    {
        if (!sessionTrusted)
        {
            return default;
        }

        AutoPlayerRunState state = status?.RunState ?? AutoPlayerRunState.Standby;
        bool needsProcessRestart = status?.NeedsProcessRestart == true;
        bool persistentCheatEffect = status?.BaseGodModeEnabled == true || status?.MapSkipEnabled == true;
        return new RunControlAvailability(
            CanStart: !needsProcessRestart
                      && !persistentCheatEffect
                      && state is AutoPlayerRunState.Standby
                          or AutoPlayerRunState.Completed
                          or AutoPlayerRunState.Faulted,
            CanPause: state == AutoPlayerRunState.Running,
            CanResume: state == AutoPlayerRunState.Paused,
            CanStop: state is AutoPlayerRunState.Running or AutoPlayerRunState.Paused);
    }
}

public sealed class ManagerSettings
{
    public const string DefaultGitHubOwner = "yingyu4451";
    public const string DefaultGitHubRepository = "Loopstructor-2-QA-Tool";
    public const string LegacyGitHubOwner = "yingyu4451";
    public const string LegacyGitHubRepository = "gui2";
    public const string PreviousGitHubRepository = "Loopstructor-2-AutoPlayer";

    public string GameRoot { get; set; } = string.Empty;
    public string UnityProjectRoot { get; set; } = string.Empty;
    public string ProfileName { get; set; } = "player-default";
    public bool ContinueExistingProfile { get; set; }
    public AutomationGameMode GameMode { get; set; } = AutomationGameMode.Common;
    public bool OverrideGameSpeed { get; set; } = true;
    public int SpeedState { get; set; }
    public int MaxRunMinutes { get; set; } = 120;
    public bool SkipStory { get; set; }
    public AutomationDecisionPriority DecisionPriority { get; set; } = AutomationDecisionPriority.CatapultPoints;
    public UiScaleMode UiScaleMode { get; set; } = UiScaleMode.System;
    public int CustomUiScalePercent { get; set; } = 100;
    public int CharacterCfgIndex { get; set; } = -1;
    public bool AutomaticSaveBackupEnabled { get; set; } = true;
    public int MaximumSaveBackups { get; set; } = 20;
    public string ActiveRoute { get; set; } = "game";
    public bool SidebarCollapsed { get; set; }
    public string SkinId { get; set; } = "skyspine";
    public string GitHubOwner { get; set; } = DefaultGitHubOwner;
    public string GitHubRepository { get; set; } = DefaultGitHubRepository;

    public void NormalizeUpdateSource()
    {
        GitHubOwner = NormalizeCoordinate(GitHubOwner, DefaultGitHubOwner);
        GitHubRepository = NormalizeCoordinate(GitHubRepository, DefaultGitHubRepository);

        if (string.Equals(GitHubOwner, LegacyGitHubOwner, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(GitHubRepository, LegacyGitHubRepository, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GitHubRepository, PreviousGitHubRepository, StringComparison.OrdinalIgnoreCase)))
        {
            GitHubOwner = DefaultGitHubOwner;
            GitHubRepository = DefaultGitHubRepository;
        }

        if (!Enum.IsDefined(UiScaleMode)) UiScaleMode = UiScaleMode.System;
        CustomUiScalePercent = Math.Clamp(CustomUiScalePercent, 75, 200);
        MaximumSaveBackups = Math.Clamp(MaximumSaveBackups, 1, 100);
        ActiveRoute = NormalizeRoute(ActiveRoute);
        SkinId = NormalizeSkin(SkinId);
    }

    private static string NormalizeCoordinate(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

    private static string NormalizeRoute(string? value)
    {
        string route = string.IsNullOrWhiteSpace(value) ? "game" : value.Trim();
        return route is "game" or "autoplay" or "vehicles" or "items" or "relics" or "battle"
            or "objects" or "spawn" or "diagnostics" or "settings"
            ? route
            : "game";
    }

    private static string NormalizeSkin(string? value) => "skyspine";
}

public enum UiScaleMode
{
    System,
    Custom
}

internal sealed class AutomationModeOption
{
    public AutomationGameMode Mode { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool Available { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Label => Available ? DisplayName : DisplayName + " · 不可用";
}

internal sealed class AutomationCharacterOption
{
    public int CfgIndex { get; init; }
    public int RuntimeIndex { get; init; }
    public int DifficultyIndex { get; init; }
    public int SuperModuleIndex { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class GameInstallValidation
{
    public bool IsValid => Errors.Count == 0;
    public string GameRoot { get; init; } = string.Empty;
    public string ExecutablePath { get; internal set; } = string.Empty;
    public string DataDirectory { get; internal set; } = string.Empty;
    public string AssemblyPath { get; internal set; } = string.Empty;
    public string AssemblySha256 { get; internal set; } = string.Empty;
    public string AssemblyMvid { get; internal set; } = string.Empty;
    public string ProductName { get; internal set; } = string.Empty;
    public string ProductVersion { get; internal set; } = string.Empty;
    public string SteamAppId { get; internal set; } = string.Empty;
    public IList<string> Errors { get; } = new List<string>();
    public IList<string> Warnings { get; } = new List<string>();
}

public enum PluginState
{
    NotInstalled,
    Enabled,
    Disabled,
    Incomplete
}

public sealed class PluginInstallStatus
{
    public PluginState State { get; init; }
    public bool BepInExPresent { get; init; }
    public bool BepInExCompatible { get; init; }
    public string PluginVersion { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class PluginOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public PluginInstallStatus? Status { get; init; }

    public static PluginOperationResult Fail(string message) => new() { Message = message };
}

public sealed class ActivationSession
{
    public required LaunchTicket Ticket { get; init; }
    public required string TicketPath { get; init; }
    public required string GameRoot { get; init; }
    public required IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
    public bool IsPersistent { get; init; }
    public AutoPlayerActivationMode ActivationMode { get; init; } = AutoPlayerActivationMode.IsolatedQa;
    public int? ProcessId { get; internal set; }
    public DateTime? ProcessStartTimeUtc { get; internal set; }
    public string ProcessInstanceId { get; internal set; } = string.Empty;
    public string LogPath => Path.Combine(Ticket.ArtifactRoot, "Player.log");

    public void DeleteTicket()
    {
        if (IsPersistent) return;
        try
        {
            if (File.Exists(TicketPath))
            {
                File.Delete(TicketPath);
            }
        }
        catch
        {
            // Ticket expiry is the final fallback if another process temporarily holds it.
        }
    }
}

public sealed class GameLaunchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public ActivationSession? Session { get; init; }
}

public sealed class PipeCallResult
{
    public bool TransportSuccess { get; init; }
    public bool RequestMayHaveExecuted { get; init; }
    public bool UsedLegacyEndpoint { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public ControlResponse? Response { get; init; }
}

public sealed class ManagerUpdateStatus
{
    public bool Success { get; set; }
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
