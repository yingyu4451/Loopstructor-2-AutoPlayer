using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class UpdateCoordinator
{
    public const string GitHubOwnerEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_GITHUB_OWNER";
    public const string GitHubRepositoryEnvironmentVariable = "LOOPSTRUCTOR_AUTOPLAYER_GITHUB_REPOSITORY";

    private static readonly Regex CoordinatePattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
    private readonly DistributionLayout _layout;

    public UpdateCoordinator(DistributionLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public bool IsConfigured(ManagerSettings settings) =>
        TryResolveCoordinates(settings, out _, out _);

    public async Task<ManagerUpdateStatus> CheckAsync(
        ManagerSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveCoordinates(settings, out string owner, out string repository))
        {
            return new ManagerUpdateStatus
            {
                CurrentVersion = CurrentVersion,
                Message = "Update source is not configured. Set the GitHub owner and repository in Manager settings or environment variables."
            };
        }

        if (!TryCreateInvocation(out ProcessStartInfo startInfo, "check"))
        {
            return new ManagerUpdateStatus
            {
                CurrentVersion = CurrentVersion,
                Message = "The independent Updater executable is missing from the release layout."
            };
        }

        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--current-version");
        startInfo.ArgumentList.Add(CurrentVersion);
        startInfo.Environment[GitHubOwnerEnvironmentVariable] = owner;
        startInfo.Environment[GitHubRepositoryEnvironmentVariable] = repository;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not create the updater process.");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            string error = await errorTask;
            return InterpretCheckResult(process.ExitCode, output, error, CurrentVersion);
        }
        catch (Exception exception)
        {
            return new ManagerUpdateStatus
            {
                CurrentVersion = CurrentVersion,
                Message = "Update check failed: " + exception.Message
            };
        }
    }

    internal static ManagerUpdateStatus InterpretCheckResult(
        int exitCode,
        string output,
        string error,
        string currentVersion)
    {
        ManagerUpdateStatus? status = null;
        string parseError = string.Empty;
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                status = JsonConvert.DeserializeObject<ManagerUpdateStatus>(output.Trim());
            }
            catch (JsonException exception)
            {
                parseError = exception.Message;
            }
        }

        if (exitCode != 0)
        {
            string detail = !string.IsNullOrWhiteSpace(error)
                ? error.Trim()
                : !string.IsNullOrWhiteSpace(status?.Message)
                    ? status.Message.Trim()
                    : parseError;
            return new ManagerUpdateStatus
            {
                CurrentVersion = currentVersion,
                LatestVersion = status?.LatestVersion ?? string.Empty,
                Message = string.IsNullOrWhiteSpace(detail)
                    ? $"Updater exited with code {exitCode} without a valid response."
                    : $"Updater exited with code {exitCode}: {detail}"
            };
        }

        if (status != null)
        {
            return status;
        }

        string invalidResponse = !string.IsNullOrWhiteSpace(error) ? error.Trim() : parseError;
        return new ManagerUpdateStatus
        {
            CurrentVersion = currentVersion,
            Message = string.IsNullOrWhiteSpace(invalidResponse)
                ? "Updater exited successfully without a valid response."
                : invalidResponse
        };
    }

    public (bool Success, string Message) StartApply(
        ManagerSettings settings,
        int? gameProcessId = null)
    {
        if (!TryResolveCoordinates(settings, out string owner, out string repository))
        {
            return (false, "Update source is not configured.");
        }

        if (!TryCreateInvocation(out ProcessStartInfo startInfo, "apply"))
        {
            return (false, "The independent Updater executable is missing from the release layout.");
        }

        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(_layout.Root);
        startInfo.ArgumentList.Add("--current-version");
        startInfo.ArgumentList.Add(CurrentVersion);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        if (gameProcessId is > 0)
        {
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(gameProcessId.Value.ToString());
        }

        startInfo.ArgumentList.Add("--restart-manager");
        startInfo.Environment[GitHubOwnerEnvironmentVariable] = owner;
        startInfo.Environment[GitHubRepositoryEnvironmentVariable] = repository;
        startInfo.CreateNoWindow = false;
        try
        {
            Process? process = Process.Start(startInfo);
            return process == null
                ? (false, "Windows did not create the updater process.")
                : (true, $"Updater started (PID {process.Id}). Manager will now close.");
        }
        catch (Exception exception)
        {
            return (false, "Updater could not be started: " + exception.Message);
        }
    }

    private bool TryCreateInvocation(out ProcessStartInfo startInfo, string command)
    {
        if (File.Exists(_layout.UpdaterExecutable))
        {
            startInfo = new ProcessStartInfo(_layout.UpdaterExecutable)
            {
                WorkingDirectory = Path.GetDirectoryName(_layout.UpdaterExecutable)!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(command);
            return true;
        }

        string updaterDll = Path.ChangeExtension(_layout.UpdaterExecutable, ".dll");
        if (File.Exists(updaterDll))
        {
            startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(updaterDll)!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(updaterDll);
            startInfo.ArgumentList.Add(command);
            return true;
        }

        startInfo = null!;
        return false;
    }

    internal static bool TryResolveCoordinates(ManagerSettings settings, out string owner, out string repository)
    {
        ArgumentNullException.ThrowIfNull(settings);
        owner = ResolveCoordinate(
            GitHubOwnerEnvironmentVariable,
            settings.GitHubOwner,
            ManagerSettings.DefaultGitHubOwner);
        repository = ResolveCoordinate(
            GitHubRepositoryEnvironmentVariable,
            settings.GitHubRepository,
            ManagerSettings.DefaultGitHubRepository);
        return CoordinatePattern.IsMatch(owner) && CoordinatePattern.IsMatch(repository);
    }

    private static string ResolveCoordinate(string environmentVariable, string configuredValue, string defaultValue)
    {
        string? environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        return string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue.Trim();
    }
}
