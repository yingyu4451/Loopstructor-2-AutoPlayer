using System.Diagnostics;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class BepInExInstaller
{
    private const string PluginDirectoryName = "Loopstructor.AutoPlayer";
    private const string PluginAssemblyName = "Loopstructor.AutoPlayer.Plugin.dll";
    private static readonly string[] RequiredPluginFiles =
    {
        PluginAssemblyName,
        "Loopstructor.AutoPlayer.Core.dll"
    };
    private static readonly string[] RequiredRuntimeFiles =
    {
        "winhttp.dll",
        "doorstop_config.ini",
        Path.Combine("BepInEx", "core", "BepInEx.dll"),
        Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll")
    };

    private readonly DistributionLayout _layout;
    private readonly BepInExConfigWriter _configWriter;

    public BepInExInstaller(DistributionLayout layout, BepInExConfigWriter? configWriter = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _configWriter = configWriter ?? new BepInExConfigWriter();
    }

    public PluginInstallStatus GetStatus(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        string pluginDirectory = Path.Combine(root, "BepInEx", "plugins", PluginDirectoryName);
        string enabledAssembly = Path.Combine(pluginDirectory, PluginAssemblyName);
        string disabledAssembly = enabledAssembly + ".disabled";
        RuntimeValidation runtime = ValidateRuntime(root);

        if (File.Exists(enabledAssembly))
        {
            bool complete = runtime.Compatible
                            && RequiredPluginFiles.All(file => File.Exists(Path.Combine(pluginDirectory, file)));
            return new PluginInstallStatus
            {
                State = complete ? PluginState.Enabled : PluginState.Incomplete,
                BepInExPresent = runtime.Present,
                BepInExCompatible = runtime.Compatible,
                PluginVersion = ReadVersion(enabledAssembly),
                Detail = complete ? "Plugin enabled with the pinned BepInEx runtime" : runtime.Detail
            };
        }

        if (File.Exists(disabledAssembly))
        {
            return new PluginInstallStatus
            {
                State = runtime.Compatible ? PluginState.Disabled : PluginState.Incomplete,
                BepInExPresent = runtime.Present,
                BepInExCompatible = runtime.Compatible,
                PluginVersion = ReadVersion(disabledAssembly),
                Detail = runtime.Compatible ? "Plugin disabled" : runtime.Detail
            };
        }

        return new PluginInstallStatus
        {
            State = PluginState.NotInstalled,
            BepInExPresent = runtime.Present,
            BepInExCompatible = runtime.Compatible,
            Detail = runtime.Present
                ? runtime.Compatible
                    ? "Pinned BepInEx runtime present; AutoPlayer plugin not installed"
                    : runtime.Detail
                : "BepInEx and plugin not installed"
        };
    }

    public async Task<PluginOperationResult> InstallAsync(
        GameInstallValidation game,
        CancellationToken cancellationToken = default)
    {
        if (!game.IsValid)
        {
            return PluginOperationResult.Fail("A strictly validated Skyspine build is required before installation.");
        }

        foreach (string required in RequiredPluginFiles)
        {
            if (!File.Exists(Path.Combine(_layout.PluginPayloadRoot, required)))
            {
                return PluginOperationResult.Fail("Plugin payload is incomplete: missing " + required);
            }
        }

        foreach (string required in RequiredRuntimeFiles)
        {
            if (!File.Exists(Path.Combine(_layout.BepInExPayloadRoot, required)))
            {
                return PluginOperationResult.Fail("BepInEx payload is incomplete: missing " + required.Replace('\\', '/'));
            }
        }

        string targetRoot = Path.GetFullPath(game.GameRoot);
        string bepinexTarget = Path.Combine(targetRoot, "BepInEx");
        RuntimeValidation runtime = ValidateRuntime(targetRoot);
        if (runtime.Present && !runtime.Compatible && !runtime.Repairable)
        {
            return PluginOperationResult.Fail(runtime.Detail);
        }

        if (!runtime.Compatible)
        {
            try
            {
                await CopyMissingTreeAsync(_layout.BepInExPayloadRoot, targetRoot, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return PluginOperationResult.Fail("BepInEx runtime installation failed: " + exception.Message);
            }

            runtime = ValidateRuntime(targetRoot);
            if (!runtime.Compatible)
            {
                return PluginOperationResult.Fail("BepInEx installation did not pass the pinned runtime check: " + runtime.Detail);
            }
        }

        string pluginTarget = Path.Combine(bepinexTarget, "plugins", PluginDirectoryName);
        string pluginParent = Path.GetDirectoryName(pluginTarget)!;
        Directory.CreateDirectory(pluginParent);
        string staging = Path.Combine(pluginParent, "." + PluginDirectoryName + ".staging-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(pluginParent, "." + PluginDirectoryName + ".backup-" + Guid.NewGuid().ToString("N"));
        List<string> installedFiles = new();
        try
        {
            Directory.CreateDirectory(staging);
            foreach (string sourcePath in Directory.GetFiles(_layout.PluginPayloadRoot, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(sourcePath);
                if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string stagedDestination = Path.Combine(staging, fileName);
                await CopyAtomicAsync(sourcePath, stagedDestination, overwrite: false, cancellationToken);
                installedFiles.Add(Path.GetRelativePath(targetRoot, Path.Combine(pluginTarget, fileName)));
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(staging);
            throw;
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(staging);
            return PluginOperationResult.Fail("Plugin payload could not be staged: " + exception.Message);
        }

        if (!RequiredPluginFiles.All(file => File.Exists(Path.Combine(staging, file))))
        {
            TryDeleteDirectory(staging);
            return PluginOperationResult.Fail("The staged plugin payload is incomplete.");
        }

        string configPath = Path.Combine(
            targetRoot,
            "BepInEx",
            "config",
            BepInExConfigWriter.PluginConfigFileName);
        string manifestPath = Path.Combine(bepinexTarget, "autoplayer", "manager-install.json");
        TextFileSnapshot configSnapshot = TextFileSnapshot.Capture(configPath);
        TextFileSnapshot manifestSnapshot = TextFileSnapshot.Capture(manifestPath);
        bool previousMoved = false;
        bool stagedMoved = false;
        try
        {
            if (Directory.Exists(pluginTarget))
            {
                Directory.Move(pluginTarget, backup);
                previousMoved = true;
            }

            Directory.Move(staging, pluginTarget);
            stagedMoved = true;
            configPath = _configWriter.Write(targetRoot, game.AssemblySha256);
            InstallManifest manifest = new()
            {
                InstalledAtUtc = DateTime.UtcNow,
                ManagerVersion = typeof(BepInExInstaller).Assembly.GetName().Version?.ToString() ?? string.Empty,
                GameAssemblySha256 = game.AssemblySha256,
                Files = installedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                ConfigPath = Path.GetRelativePath(targetRoot, configPath)
            };
            AtomicFile.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }
        catch (Exception exception)
        {
            string failed = staging + ".failed";
            try
            {
                if (stagedMoved && Directory.Exists(pluginTarget)) Directory.Move(pluginTarget, failed);
                if (previousMoved && Directory.Exists(backup) && !Directory.Exists(pluginTarget))
                {
                    Directory.Move(backup, pluginTarget);
                }

                configSnapshot.Restore();
                manifestSnapshot.Restore();
            }
            catch (Exception rollbackException)
            {
                return PluginOperationResult.Fail(
                    "Plugin installation failed and rollback was incomplete: " + exception.Message +
                    " Rollback error: " + rollbackException.Message);
            }
            finally
            {
                TryDeleteDirectory(staging);
                TryDeleteDirectory(failed);
            }

            return PluginOperationResult.Fail("Plugin installation failed; the previous plugin was restored: " + exception.Message);
        }

        TryDeleteDirectory(backup);

        PluginInstallStatus status = GetStatus(targetRoot);
        return new PluginOperationResult
        {
            Success = status.State == PluginState.Enabled,
            Message = status.State == PluginState.Enabled
                ? "BepInEx AutoPlayer plugin installed and enabled."
                : "Plugin files were installed, but validation remains incomplete.",
            Status = status
        };
    }

    public PluginOperationResult SetEnabled(string gameRoot, bool enabled)
    {
        string pluginDirectory = Path.Combine(Path.GetFullPath(gameRoot), "BepInEx", "plugins", PluginDirectoryName);
        string active = Path.Combine(pluginDirectory, PluginAssemblyName);
        string disabled = active + ".disabled";
        try
        {
            if (enabled)
            {
                RuntimeValidation runtime = ValidateRuntime(gameRoot);
                if (!runtime.Compatible)
                {
                    return PluginOperationResult.Fail(runtime.Detail);
                }

                if (File.Exists(active))
                {
                    return Success("Plugin is already enabled.", gameRoot);
                }

                if (!File.Exists(disabled))
                {
                    return PluginOperationResult.Fail("No disabled plugin assembly was found.");
                }

                File.Move(disabled, active);
                return Success("Plugin enabled. It will activate only with a valid launch ticket.", gameRoot);
            }

            if (File.Exists(disabled))
            {
                return Success("Plugin is already disabled.", gameRoot);
            }

            if (!File.Exists(active))
            {
                return PluginOperationResult.Fail("No installed plugin assembly was found.");
            }

            File.Move(active, disabled);
            return Success("Plugin disabled. Restart the game to unload it.", gameRoot);
        }
        catch (Exception exception)
        {
            return PluginOperationResult.Fail("Plugin state could not be changed: " + exception.Message);
        }
    }

    public PluginOperationResult Uninstall(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        string pluginDirectory = Path.Combine(root, "BepInEx", "plugins", PluginDirectoryName);
        string manifestPath = Path.Combine(root, "BepInEx", "autoplayer", "manager-install.json");
        try
        {
            HashSet<string> managedFiles = new(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.dll"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.dll.disabled"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.pdb"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Plugin.xml"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Core.dll"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Core.pdb"),
                Path.Combine(pluginDirectory, "Loopstructor.AutoPlayer.Core.xml")
            };
            if (File.Exists(manifestPath))
            {
                InstallManifest? manifest = JsonConvert.DeserializeObject<InstallManifest>(File.ReadAllText(manifestPath));
                foreach (string relative in manifest?.Files ?? Array.Empty<string>())
                {
                    string full = Path.GetFullPath(Path.Combine(root, relative));
                    if (!string.Equals(Path.GetDirectoryName(full), pluginDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return PluginOperationResult.Fail("Install manifest contains a path outside the AutoPlayer plugin directory.");
                    }

                    managedFiles.Add(full);
                    if (string.Equals(Path.GetFileName(full), PluginAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        managedFiles.Add(full + ".disabled");
                    }
                }
            }

            if (Directory.Exists(pluginDirectory))
            {
                foreach (string file in managedFiles)
                {
                    if (File.Exists(file)) File.Delete(file);
                }

                if (!Directory.EnumerateFileSystemEntries(pluginDirectory).Any())
                {
                    Directory.Delete(pluginDirectory);
                }
            }

            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            string config = Path.Combine(root, "BepInEx", "config", BepInExConfigWriter.PluginConfigFileName);
            if (File.Exists(config))
            {
                File.Delete(config);
            }

            return Success("AutoPlayer plugin removed. The shared BepInEx runtime was retained.", root);
        }
        catch (Exception exception)
        {
            return PluginOperationResult.Fail("Plugin could not be removed completely: " + exception.Message);
        }
    }

    private PluginOperationResult Success(string message, string gameRoot)
    {
        return new PluginOperationResult { Success = true, Message = message, Status = GetStatus(gameRoot) };
    }

    private static string ReadVersion(string assemblyPath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private RuntimeValidation ValidateRuntime(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        List<string> missingPayload = RequiredRuntimeFiles
            .Where(relative => !File.Exists(Path.Combine(_layout.BepInExPayloadRoot, relative)))
            .ToList();
        int existingCount = RequiredRuntimeFiles.Count(relative => File.Exists(Path.Combine(root, relative)));
        bool present = existingCount > 0;
        if (missingPayload.Count > 0)
        {
            return new RuntimeValidation(
                present,
                Compatible: false,
                Repairable: false,
                "Packaged BepInEx runtime is incomplete: " + string.Join(", ", missingPayload.Select(path => path.Replace('\\', '/'))));
        }

        List<string> missing = new();
        List<string> mismatched = new();
        foreach (string relative in RequiredRuntimeFiles)
        {
            string target = Path.Combine(root, relative);
            string payload = Path.Combine(_layout.BepInExPayloadRoot, relative);
            if (!File.Exists(target))
            {
                missing.Add(relative);
            }
            else if (!HashesEqual(target, payload))
            {
                mismatched.Add(relative);
            }
        }

        if (mismatched.Count > 0)
        {
            return new RuntimeValidation(
                present,
                Compatible: false,
                Repairable: false,
                "Existing BepInEx runtime is not the packaged Windows x64 build; conflicting files: " +
                string.Join(", ", mismatched.Select(path => path.Replace('\\', '/'))));
        }

        if (missing.Count > 0)
        {
            return new RuntimeValidation(
                present,
                Compatible: false,
                Repairable: true,
                present
                    ? "Existing BepInEx runtime is incomplete; missing: " +
                      string.Join(", ", missing.Select(path => path.Replace('\\', '/')))
                    : "Pinned BepInEx runtime is not installed.");
        }

        return new RuntimeValidation(true, Compatible: true, Repairable: false, "Pinned BepInEx runtime verified.");
    }

    private static bool HashesEqual(string first, string second)
    {
        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream firstStream = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] firstHash = sha256.ComputeHash(firstStream);
            using FileStream secondStream = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] secondHash = sha256.ComputeHash(secondStream);
            return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A retained staging or backup directory is safer than deleting an uncertain path.
        }
    }

    private static async Task CopyMissingTreeAsync(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (string source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, source);
            string destination = Path.Combine(destinationRoot, relative);
            if (!File.Exists(destination))
            {
                await CopyAtomicAsync(source, destination, overwrite: false, cancellationToken);
            }
        }
    }

    private static async Task CopyAtomicAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    private sealed class InstallManifest
    {
        public DateTime InstalledAtUtc { get; set; }
        public string ManagerVersion { get; set; } = string.Empty;
        public string GameAssemblySha256 { get; set; } = string.Empty;
        public string[] Files { get; set; } = Array.Empty<string>();
        public string ConfigPath { get; set; } = string.Empty;
    }

    private sealed record RuntimeValidation(bool Present, bool Compatible, bool Repairable, string Detail);

    private sealed record TextFileSnapshot(string Path, bool Existed, string Content)
    {
        public static TextFileSnapshot Capture(string path) => File.Exists(path)
            ? new TextFileSnapshot(path, true, File.ReadAllText(path))
            : new TextFileSnapshot(path, false, string.Empty);

        public void Restore()
        {
            if (Existed)
            {
                AtomicFile.WriteAllText(Path, Content);
            }
            else if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
