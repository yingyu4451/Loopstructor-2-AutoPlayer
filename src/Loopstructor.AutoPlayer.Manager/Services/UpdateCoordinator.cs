using System.Diagnostics;
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

    public string CurrentVersion => ManagerProductInfo.Version;

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
                Message = "更新源未配置。请在 Manager 设置或环境变量中配置 GitHub 所有者与仓库名称。"
            };
        }

        if (!TryCreateInvocation(out ProcessStartInfo startInfo, "check"))
        {
            return new ManagerUpdateStatus
            {
                CurrentVersion = CurrentVersion,
                Message = "发布目录中缺少独立的 Updater 可执行文件。"
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
                ?? throw new InvalidOperationException("Windows 未能创建 Updater 进程。");
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
                Message = "检查更新失败。详细信息：" + exception.Message
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
                    ? $"Updater 已退出，退出代码为 {exitCode}，但未返回有效响应。"
                    : $"Updater 已退出，退出代码为 {exitCode}。详细信息：{detail}"
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
                ? "Updater 已正常退出，但未返回有效响应。"
                : "Updater 返回的响应无效。详细信息：" + invalidResponse
        };
    }

    public (bool Success, string Message) StartApply(
        ManagerSettings settings,
        int? gameProcessId = null)
    {
        if (!TryResolveCoordinates(settings, out string owner, out string repository))
        {
            return (false, "更新源未配置。");
        }

        if (!TryCreateInvocation(out ProcessStartInfo startInfo, "apply"))
        {
            return (false, "发布目录中缺少独立的 Updater 可执行文件。");
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
                ? (false, "Windows 未能创建 Updater 进程。")
                : (true, $"Updater 已启动（PID {process.Id}），Manager 现在将关闭。");
        }
        catch (Exception exception)
        {
            return (false, "无法启动 Updater。详细信息：" + exception.Message);
        }
    }

    internal bool TryCreateInvocation(out ProcessStartInfo startInfo, string command)
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
