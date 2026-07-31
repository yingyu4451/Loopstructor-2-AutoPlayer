using System.Diagnostics;
using System.Reflection;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class SelfRelocator
{
    private const string TemporaryFolderName = "LoopstructorAutoPlayerUpdater";
    private const string ManagerFolderName = "manager";
    private const string UpdaterAssemblyName = "Loopstructor.AutoPlayer.Updater";
    private const string UpdaterExecutableName = UpdaterAssemblyName + ".exe";
    private const string UpdaterDllName = UpdaterAssemblyName + ".dll";
    private const string UpdaterDepsName = UpdaterAssemblyName + ".deps.json";
    private const string UpdaterRuntimeConfigName = UpdaterAssemblyName + ".runtimeconfig.json";

    public Process RelaunchFromTemporaryCopy(
        IReadOnlyList<string> originalArguments,
        bool redirectOutput = false)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);

        string sourceRoot = NormalizeDirectory(AppContext.BaseDirectory);
        string entryAssembly = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
        string processPath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法确定更新器进程路径。");
        string temporaryBase = NormalizeDirectory(Path.Combine(Path.GetTempPath(), TemporaryFolderName));
        Directory.CreateDirectory(temporaryBase);
        EnsureRegularDirectory(temporaryBase, "更新器临时目录不能是重解析点。");
        CleanupOldCopies(temporaryBase, sourceRoot);

        string destinationRoot = Path.Combine(temporaryBase, "updater-" + Guid.NewGuid().ToString("N"));
        RelocationPlan plan = CreateRelocationPlan(
            sourceRoot,
            entryAssembly,
            processPath,
            destinationRoot);
        StagePlan(plan);

        ProcessStartInfo startInfo = CreateStartInfo(plan, originalArguments, redirectOutput);
        Process? process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Windows 未能启动更新器临时副本。");
        }

        return process;
    }

    internal static RelocationPlan CreateRelocationPlan(
        string sourceBaseDirectory,
        string entryAssemblyPath,
        string processPath,
        string destinationRoot)
    {
        string sourceRoot = NormalizeDirectory(sourceBaseDirectory);
        string destination = NormalizeDirectory(destinationRoot);
        string process = NormalizeFile(processPath, "无法确定更新器进程路径。");

        EnsureRegularDirectory(sourceRoot, "更新器源目录不能是重解析点。");
        EnsureDirectoriesDoNotOverlap(sourceRoot, destination);

        if (IsSharedManagerRuntimeCandidate(sourceRoot, process))
        {
            return CreateSharedManagerRuntimePlan(
                sourceRoot,
                entryAssemblyPath,
                process,
                destination);
        }

        return CreateStandalonePlan(sourceRoot, entryAssemblyPath, process, destination);
    }

    internal static ProcessStartInfo CreateStartInfo(
        RelocationPlan plan,
        IReadOnlyList<string> originalArguments,
        bool redirectOutput)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(originalArguments);

        ProcessStartInfo startInfo = new(plan.ExecutablePath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };
        if (!string.IsNullOrWhiteSpace(plan.ManagedEntryAssemblyPath))
        {
            startInfo.ArgumentList.Add(plan.ManagedEntryAssemblyPath);
        }

        foreach (string argument in originalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--staged-run");
        return startInfo;
    }

    internal static void StagePlan(RelocationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string destinationRoot = NormalizeDirectory(plan.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        EnsureRegularDirectory(destinationRoot, "更新器临时副本目录不能是重解析点。");

        foreach (RelocationDirectory directory in plan.Directories)
        {
            string destination = NormalizeDirectory(directory.DestinationDirectory);
            EnsureContainedPath(destinationRoot, destination, allowEqual: true, "临时副本目录超出允许范围。");
            CopyDirectory(directory.SourceDirectory, destination);
        }
    }

    private static RelocationPlan CreateSharedManagerRuntimePlan(
        string sourceRoot,
        string entryAssemblyPath,
        string processPath,
        string destinationRoot)
    {
        if (!string.Equals(Path.GetFileName(sourceRoot), ManagerFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("共享运行时更新器必须位于 manager 目录中。");
        }

        string releaseRoot = Directory.GetParent(sourceRoot)?.FullName
                             ?? throw new InvalidOperationException("无法确定更新器发布根目录。");
        releaseRoot = NormalizeDirectory(releaseRoot);
        EnsureRegularDirectory(releaseRoot, "更新器发布根目录不能是重解析点。");
        EnsureDirectChild(releaseRoot, sourceRoot, ManagerFolderName);

        string expectedExecutable = Path.Combine(sourceRoot, UpdaterExecutableName);
        if (!PathsEqual(processPath, expectedExecutable))
        {
            throw new InvalidOperationException("共享运行时更新器进程不在预期的 manager 目录中。");
        }

        string entryAssembly = NormalizeFile(entryAssemblyPath, "无法确定更新器入口程序集路径。");
        string expectedAssembly = Path.Combine(sourceRoot, UpdaterDllName);
        if (!PathsEqual(entryAssembly, expectedAssembly))
        {
            throw new InvalidOperationException("共享运行时更新器入口程序集不在预期的 manager 目录中。");
        }

        EnsureRegularFile(expectedExecutable, "找不到共享运行时更新器程序。");
        EnsureRegularFile(expectedAssembly, "找不到共享运行时更新器程序集。");
        EnsureRegularFile(
            Path.Combine(sourceRoot, UpdaterDepsName),
            "找不到共享运行时更新器依赖清单。");
        EnsureRegularFile(
            Path.Combine(sourceRoot, UpdaterRuntimeConfigName),
            "找不到共享运行时更新器运行配置。");
        ValidateSourceTree(sourceRoot);

        string copiedManager = Path.Combine(destinationRoot, ManagerFolderName);
        return new RelocationPlan(
            destinationRoot,
            Path.Combine(copiedManager, UpdaterExecutableName),
            null,
            copiedManager,
            new[] { new RelocationDirectory(sourceRoot, copiedManager) },
            true);
    }

    private static RelocationPlan CreateStandalonePlan(
        string sourceRoot,
        string entryAssemblyPath,
        string processPath,
        string destinationRoot)
    {
        ValidateSourceTree(sourceRoot);
        RelocationDirectory[] directories = { new(sourceRoot, destinationRoot) };

        if (IsContainedPath(sourceRoot, processPath, allowEqual: false))
        {
            EnsureRegularFile(processPath, "找不到更新器程序。");
            string copiedProcess = Path.Combine(
                destinationRoot,
                GetContainedRelativePath(sourceRoot, processPath));
            return new RelocationPlan(
                destinationRoot,
                copiedProcess,
                null,
                destinationRoot,
                directories,
                false);
        }

        string entryAssembly = NormalizeFile(entryAssemblyPath, "无法确定更新器入口程序集路径。");
        EnsureContainedPath(sourceRoot, entryAssembly, allowEqual: false, "更新器入口程序集超出源目录范围。");
        EnsureRegularFile(entryAssembly, "找不到更新器入口程序集。");
        string copiedAssembly = Path.Combine(
            destinationRoot,
            GetContainedRelativePath(sourceRoot, entryAssembly));
        string sourceExecutable = Path.ChangeExtension(entryAssembly, ".exe");
        if (File.Exists(sourceExecutable))
        {
            EnsureRegularFile(sourceExecutable, "更新器程序不能是重解析点。");
            string copiedExecutable = Path.Combine(
                destinationRoot,
                GetContainedRelativePath(sourceRoot, sourceExecutable));
            return new RelocationPlan(
                destinationRoot,
                copiedExecutable,
                null,
                destinationRoot,
                directories,
                false);
        }

        EnsureRegularFile(processPath, "找不到 .NET 主机程序。");
        return new RelocationPlan(
            destinationRoot,
            processPath,
            copiedAssembly,
            destinationRoot,
            directories,
            false);
    }

    private static bool IsSharedManagerRuntimeCandidate(string sourceRoot, string processPath)
    {
        bool sourceLooksShared = string.Equals(
            Path.GetFileName(sourceRoot),
            ManagerFolderName,
            StringComparison.OrdinalIgnoreCase);
        bool processLooksShared = string.Equals(
            Path.GetFileName(processPath),
            UpdaterExecutableName,
            StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(Path.GetDirectoryName(processPath)),
                ManagerFolderName,
                StringComparison.OrdinalIgnoreCase);
        return sourceLooksShared || processLooksShared;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        string source = NormalizeDirectory(sourceRoot);
        string destination = NormalizeDirectory(destinationRoot);
        EnsureDirectoriesDoNotOverlap(source, destination);
        EnsureRegularDirectory(source, "更新器源目录不能是重解析点。");

        Directory.CreateDirectory(destination);
        EnsureRegularDirectory(destination, "更新器临时副本目录不能是重解析点。");
        CopyDirectoryContents(source, destination);
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularFile(file, "更新器源目录包含文件重解析点：" + file);
            string target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
        }

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularDirectory(directory, "更新器源目录包含目录重解析点：" + directory);
            string target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            EnsureRegularDirectory(target, "更新器临时副本目录不能是重解析点。");
            CopyDirectoryContents(directory, target);
        }
    }

    private static void ValidateSourceTree(string sourceRoot)
    {
        EnsureRegularDirectory(sourceRoot, "更新器源目录不能是重解析点。");
        ValidateDirectoryContents(sourceRoot);
    }

    private static void ValidateDirectoryContents(string source)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularFile(file, "更新器源目录包含文件重解析点：" + file);
        }

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularDirectory(directory, "更新器源目录包含目录重解析点：" + directory);
            ValidateDirectoryContents(directory);
        }
    }

    private static void CleanupOldCopies(string temporaryBase, string currentSource)
    {
        string safeBase = NormalizeDirectory(temporaryBase);
        foreach (string directory in Directory.EnumerateDirectories(
                     safeBase,
                     "updater-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                string full = NormalizeDirectory(directory);
                EnsureContainedPath(safeBase, full, allowEqual: false, "临时副本目录超出允许范围。");
                if (PathsEqual(full, currentSource)
                    || Directory.GetCreationTimeUtc(full) > DateTime.UtcNow.AddDays(-7))
                {
                    continue;
                }

                ValidateSourceTree(full);
                Directory.Delete(full, recursive: true);
            }
            catch
            {
                // Old temporary copies are non-authoritative and may still be in use.
            }
        }
    }

    private static void EnsureDirectChild(string parent, string child, string expectedName)
    {
        string expected = NormalizeDirectory(Path.Combine(parent, expectedName));
        if (!PathsEqual(expected, child))
        {
            throw new InvalidOperationException("共享运行时目录结构无效。");
        }

        EnsureContainedPath(parent, child, allowEqual: false, "共享运行时目录超出发布根目录范围。");
    }

    private static string GetContainedRelativePath(string parent, string child)
    {
        EnsureContainedPath(parent, child, allowEqual: false, "更新器文件超出源目录范围。");
        string relative = Path.GetRelativePath(parent, child);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("无法创建安全的更新器相对路径。");
        }

        return relative;
    }

    private static void EnsureDirectoriesDoNotOverlap(string first, string second)
    {
        if (IsContainedPath(first, second, allowEqual: true)
            || IsContainedPath(second, first, allowEqual: true))
        {
            throw new InvalidOperationException("更新器源目录与临时副本目录不能重叠。");
        }
    }

    private static void EnsureContainedPath(
        string parent,
        string child,
        bool allowEqual,
        string message)
    {
        if (!IsContainedPath(parent, child, allowEqual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsContainedPath(string parent, string child, bool allowEqual)
    {
        string normalizedParent = NormalizeDirectory(parent);
        string normalizedChild = Path.GetFullPath(child);
        if (allowEqual && PathsEqual(normalizedParent, normalizedChild))
        {
            return true;
        }

        string prefix = Path.EndsInDirectorySeparator(normalizedParent)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("更新器目录路径不能为空。");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string NormalizeFile(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(message);
        }

        return Path.GetFullPath(path);
    }

    private static void EnsureRegularDirectory(string path, string message)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException("找不到目录：" + path);
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureRegularFile(string path, string message)
    {
        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(message, path);
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(message);
        }
    }

    internal sealed record RelocationPlan(
        string DestinationRoot,
        string ExecutablePath,
        string? ManagedEntryAssemblyPath,
        string WorkingDirectory,
        IReadOnlyList<RelocationDirectory> Directories,
        bool UsesSharedManagerRuntime);

    internal sealed record RelocationDirectory(
        string SourceDirectory,
        string DestinationDirectory);
}
