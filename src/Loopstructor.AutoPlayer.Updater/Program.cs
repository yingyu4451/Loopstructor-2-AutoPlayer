using System.Net;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Updater;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        bool wantsJson = args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        UpdateCommandOptions? options = null;
        try
        {
            options = UpdateCommandOptions.Parse(args);
            if (options.Command == UpdateCommand.Apply && !options.StagedRun)
            {
                SelfRelocator relocator = new();
                relocator.RelaunchFromTemporaryCopy(args);
                return 0;
            }

            UpdateConfigurationLoader configurationLoader = new();
            LoadedUpdateConfiguration configuration = configurationLoader.Load(options);
            using SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20)
            };
            using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromMinutes(10) };
            GitHubReleaseClient releaseClient = new(httpClient, configuration.Source, configuration.GitHubToken);
            ResolvedUpdate update = await releaseClient.ResolveLatestAsync();
            if (!SemanticVersion.TryParse(options.CurrentVersion, out SemanticVersion? current)
                || !SemanticVersion.TryParse(update.Manifest.Version, out SemanticVersion? latest))
            {
                throw new InvalidDataException("Current or latest release version is invalid.");
            }

            bool updateAvailable = latest!.CompareTo(current) > 0;
            if (options.Command == UpdateCommand.Check)
            {
                UpdaterResult checkResult = new()
                {
                    Success = true,
                    UpdateAvailable = updateAvailable,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = updateAvailable
                        ? $"AutoPlayer {update.Manifest.Version} is available."
                        : "AutoPlayer is already up to date."
                };
                WriteResult(checkResult, options.JsonOutput);
                return 0;
            }

            if (!updateAvailable)
            {
                WriteResult(new UpdaterResult
                {
                    Success = true,
                    CurrentVersion = options.CurrentVersion,
                    LatestVersion = update.Manifest.Version,
                    Message = "No update is required."
                }, options.JsonOutput);
                return 0;
            }

            return await ApplyAsync(options, update, releaseClient);
        }
        catch (Exception exception)
        {
            UpdaterResult failure = new()
            {
                CurrentVersion = options?.CurrentVersion ?? string.Empty,
                Message = exception.Message
            };
            WriteResult(failure, options?.JsonOutput ?? wantsJson);
            return 1;
        }
    }

    private static async Task<int> ApplyAsync(
        UpdateCommandOptions options,
        ResolvedUpdate update,
        GitHubReleaseClient releaseClient)
    {
        ReleasePackageValidator packageValidator = new();
        packageValidator.Validate(options.TargetRoot, validateTargetSafety: true);
        TransactionalInstaller installer = new(
            packageValidator,
            TransactionalInstaller.GetDefaultJournalPath(options.TargetRoot));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "LoopstructorAutoPlayerUpdater",
            "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string packagePath = Path.Combine(temporaryRoot, update.Manifest.AssetName);
        string stagingRoot = installer.CreateStagingRoot(options.TargetRoot);
        bool replacementStarted = false;
        try
        {
            WriteProgress(options, $"Downloading {update.PackageAsset.Name}...");
            await releaseClient.DownloadVerifiedPackageAsync(update, packagePath);
            WriteProgress(options, "Package SHA-256 verified. Extracting to staging...");
            SecureZipExtractor extractor = new();
            extractor.ExtractReleasePackage(packagePath, stagingRoot);
            packageValidator.Validate(stagingRoot, update.Manifest.Version);

            ProcessWaiter processWaiter = new();
            await processWaiter.WaitForExitAsync(
                options.WaitProcessIds,
                TimeSpan.FromSeconds(options.WaitTimeoutSeconds),
                message => WriteProgress(options, message));

            using UpdateTargetLock targetLock = UpdateTargetLock.Acquire(
                options.TargetRoot,
                TimeSpan.FromSeconds(30));
            string recovery = installer.RecoverIncomplete(options.TargetRoot);
            if (!string.IsNullOrWhiteSpace(recovery)) WriteProgress(options, recovery);
            replacementStarted = true;
            string backup = installer.Apply(stagingRoot, options.TargetRoot, update.Manifest.Version);
            UpdaterResult result = new()
            {
                Success = true,
                UpdateAvailable = false,
                CurrentVersion = options.CurrentVersion,
                LatestVersion = update.Manifest.Version,
                Message = $"AutoPlayer updated to {update.Manifest.Version}.",
                BackupDirectory = backup
            };
            if (options.RestartManager)
            {
                try
                {
                    ManagerRestarter restarter = new();
                    restarter.Restart(options.TargetRoot);
                }
                catch (Exception exception)
                {
                    result.Message += " Manager restart failed: " + exception.Message;
                }
            }

            WriteResult(result, options.JsonOutput);
            return 0;
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, Path.Combine(Path.GetTempPath(), "LoopstructorAutoPlayerUpdater"));
            if (!replacementStarted)
            {
                string targetParent = Directory.GetParent(ReleasePackageValidator.NormalizeRoot(options.TargetRoot))!.FullName;
                DeleteTemporaryDirectory(stagingRoot, targetParent, ".LoopstructorAutoPlayer-staging-");
            }
        }
    }

    private static void WriteResult(UpdaterResult result, bool json)
    {
        if (json)
        {
            Console.Out.Write(JsonSerializer.Serialize(result, OutputJson));
            return;
        }

        Console.WriteLine(result.Success ? result.Message : "Update failed: " + result.Message);
        if (!string.IsNullOrWhiteSpace(result.BackupDirectory))
        {
            Console.WriteLine("Previous release backup: " + result.BackupDirectory);
        }
    }

    private static void WriteProgress(UpdateCommandOptions options, string message)
    {
        if (!options.JsonOutput) Console.WriteLine(message);
    }

    private static void DeleteTemporaryDirectory(string path, string allowedParent, string? requiredPrefix = null)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            string parent = Path.GetFullPath(allowedParent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            string prefix = parent + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(requiredPrefix)
                    && !Path.GetFileName(full).StartsWith(requiredPrefix, StringComparison.Ordinal)))
            {
                return;
            }

            Directory.Delete(full, recursive: true);
        }
        catch
        {
            // Temporary download and pre-transaction staging cleanup is best effort.
        }
    }
}
