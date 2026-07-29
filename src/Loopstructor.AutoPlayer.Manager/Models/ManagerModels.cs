using Loopstructor.AutoPlayer.Core;
using System.Drawing;

namespace Loopstructor.AutoPlayer.Manager.Models;

public sealed class ManagerLaunchOptions
{
    public bool DemoMode { get; private set; }
    public bool DemoRestartRequired { get; private set; }
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
                case "--demo":
                    result.DemoMode = true;
                    break;
                case "--demo-restart-required":
                    result.DemoMode = true;
                    result.DemoRestartRequired = true;
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
                        && width is >= 1100 and <= 2560
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
        return new RunControlAvailability(
            CanStart: !needsProcessRestart
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
    public const string DefaultGitHubRepository = "gui2";

    public string GameRoot { get; set; } = string.Empty;
    public string ProfileName { get; set; } = "qa-default";
    public bool ContinueExistingProfile { get; set; }
    public AutomationGameMode GameMode { get; set; } = AutomationGameMode.Common;
    public int SpeedState { get; set; } = 2;
    public int MaxRunMinutes { get; set; } = 120;
    public string GitHubOwner { get; set; } = DefaultGitHubOwner;
    public string GitHubRepository { get; set; } = DefaultGitHubRepository;
    public bool CheckUpdatesOnStart { get; set; } = true;
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
    public int? ProcessId { get; internal set; }
    public string LogPath => Path.Combine(Ticket.ArtifactRoot, "Player.log");

    public void DeleteTicket()
    {
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
