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
            bool currentPayload = InstalledPluginMatchesPayload(pluginDirectory, enabledAssembly);
            bool complete = runtime.Compatible && currentPayload;
            return new PluginInstallStatus
            {
                State = complete ? PluginState.Enabled : PluginState.Incomplete,
                BepInExPresent = runtime.Present,
                BepInExCompatible = runtime.Compatible,
                PluginVersion = ReadVersion(enabledAssembly),
                Detail = complete
                    ? "插件已启用，插件载荷与固定版本的 BepInEx 运行时均已验证"
                    : runtime.Compatible
                        ? "游戏目录中的 AutoPlayer 插件不是当前 Manager 携带的版本，请重新安装插件。"
                        : runtime.Detail
            };
        }

        if (File.Exists(disabledAssembly))
        {
            bool currentPayload = InstalledPluginMatchesPayload(pluginDirectory, disabledAssembly);
            bool complete = runtime.Compatible && currentPayload;
            return new PluginInstallStatus
            {
                State = complete ? PluginState.Disabled : PluginState.Incomplete,
                BepInExPresent = runtime.Present,
                BepInExCompatible = runtime.Compatible,
                PluginVersion = ReadVersion(disabledAssembly),
                Detail = complete
                    ? "插件已停用"
                    : runtime.Compatible
                        ? "游戏目录中的已停用插件不是当前 Manager 携带的版本，请重新安装插件。"
                        : runtime.Detail
            };
        }

        return new PluginInstallStatus
        {
            State = PluginState.NotInstalled,
            BepInExPresent = runtime.Present,
            BepInExCompatible = runtime.Compatible,
            Detail = runtime.Present
                ? runtime.Compatible
                    ? "已安装固定版本的 BepInEx 运行时，但未安装 AutoPlayer 插件"
                    : runtime.Detail
                : "尚未安装 BepInEx 和 AutoPlayer 插件"
        };
    }

    public async Task<PluginOperationResult> InstallAsync(
        GameInstallValidation game,
        CancellationToken cancellationToken = default)
    {
        if (!game.IsValid)
        {
            return PluginOperationResult.Fail("安装前必须严格验证 Skyspine 构建。");
        }

        foreach (string required in RequiredPluginFiles)
        {
            if (!File.Exists(Path.Combine(_layout.PluginPayloadRoot, required)))
            {
                return PluginOperationResult.Fail("插件载荷不完整，缺少：" + required);
            }
        }

        foreach (string required in RequiredRuntimeFiles)
        {
            if (!File.Exists(Path.Combine(_layout.BepInExPayloadRoot, required)))
            {
                return PluginOperationResult.Fail("BepInEx 载荷不完整，缺少：" + required.Replace('\\', '/'));
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
                return PluginOperationResult.Fail("BepInEx 运行时安装失败。详细信息：" + exception.Message);
            }

            runtime = ValidateRuntime(targetRoot);
            if (!runtime.Compatible)
            {
                return PluginOperationResult.Fail("BepInEx 安装未通过固定版本运行时校验：" + runtime.Detail);
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
            return PluginOperationResult.Fail("无法暂存插件载荷。详细信息：" + exception.Message);
        }

        if (!RequiredPluginFiles.All(file => File.Exists(Path.Combine(staging, file))))
        {
            TryDeleteDirectory(staging);
            return PluginOperationResult.Fail("暂存的插件载荷不完整。");
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
                    "插件安装失败且回滚未完成。安装错误：" + exception.Message +
                    "；回滚错误：" + rollbackException.Message);
            }
            finally
            {
                TryDeleteDirectory(staging);
                TryDeleteDirectory(failed);
            }

            return PluginOperationResult.Fail("插件安装失败，已恢复原插件。详细信息：" + exception.Message);
        }

        TryDeleteDirectory(backup);

        PluginInstallStatus status = GetStatus(targetRoot);
        return new PluginOperationResult
        {
            Success = status.State == PluginState.Enabled,
            Message = status.State == PluginState.Enabled
                ? "BepInEx AutoPlayer 插件已安装并启用。"
                : "插件文件已安装，但校验尚未完成。",
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
                    return Success("插件已启用，无需重复操作。", gameRoot);
                }

                if (!File.Exists(disabled))
                {
                    return PluginOperationResult.Fail("未找到已停用的插件程序集。");
                }

                File.Move(disabled, active);
                return Success("插件已启用；仅会在启动授权有效时激活。", gameRoot);
            }

            if (File.Exists(disabled))
            {
                return Success("插件已停用，无需重复操作。", gameRoot);
            }

            if (!File.Exists(active))
            {
                return PluginOperationResult.Fail("未找到已安装的插件程序集。");
            }

            File.Move(active, disabled);
            return Success("插件已停用，请重启游戏以卸载已加载的插件。", gameRoot);
        }
        catch (Exception exception)
        {
            return PluginOperationResult.Fail("无法更改插件状态。详细信息：" + exception.Message);
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
                        return PluginOperationResult.Fail("安装清单包含 AutoPlayer 插件目录之外的路径。");
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

            return Success("AutoPlayer 插件已删除；共享的 BepInEx 运行时已保留。", root);
        }
        catch (Exception exception)
        {
            return PluginOperationResult.Fail("无法完全删除插件。详细信息：" + exception.Message);
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

    private bool InstalledPluginMatchesPayload(string pluginDirectory, string pluginAssemblyPath)
    {
        foreach (string file in RequiredPluginFiles)
        {
            string installed = string.Equals(file, PluginAssemblyName, StringComparison.OrdinalIgnoreCase)
                ? pluginAssemblyPath
                : Path.Combine(pluginDirectory, file);
            string payload = Path.Combine(_layout.PluginPayloadRoot, file);
            if (!File.Exists(installed) || !File.Exists(payload) || !HashesEqual(installed, payload))
            {
                return false;
            }
        }

        return true;
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
                "发布包内的 BepInEx 运行时不完整，缺少：" + string.Join(", ", missingPayload.Select(path => path.Replace('\\', '/'))));
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
                "现有 BepInEx 运行时不是发布包提供的 Windows x64 版本；冲突文件：" +
                string.Join(", ", mismatched.Select(path => path.Replace('\\', '/'))));
        }

        if (missing.Count > 0)
        {
            return new RuntimeValidation(
                present,
                Compatible: false,
                Repairable: true,
                present
                    ? "现有 BepInEx 运行时不完整，缺少：" +
                      string.Join(", ", missing.Select(path => path.Replace('\\', '/')))
                    : "尚未安装固定版本的 BepInEx 运行时。");
        }

        return new RuntimeValidation(true, Compatible: true, Repairable: false, "固定版本的 BepInEx 运行时已验证。");
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
