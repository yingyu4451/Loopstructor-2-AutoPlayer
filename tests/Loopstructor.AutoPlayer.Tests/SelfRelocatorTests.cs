using System.Diagnostics;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class SelfRelocatorTests
{
    private const string UpdaterName = "Loopstructor.AutoPlayer.Updater";

    [Fact]
    public void SharedManagerRuntime_StagesWholeManagerAndStartsCopiedAppHost()
    {
        using TestDirectory test = new();
        string releaseRoot = test.CreateDirectory("release");
        string manager = test.CreateDirectory("release", "manager");
        string executable = test.CreateFile("release", "manager", UpdaterName + ".exe");
        string assembly = test.CreateFile("release", "manager", UpdaterName + ".dll");
        test.CreateFile("release", "manager", UpdaterName + ".deps.json");
        test.CreateFile("release", "manager", UpdaterName + ".runtimeconfig.json");
        string runtimeFile = test.CreateFile("release", "manager", "shared", "runtime.dll");
        string destination = Path.Combine(test.Root, "temporary", "updater-copy");

        SelfRelocator.RelocationPlan plan = SelfRelocator.CreateRelocationPlan(
            manager,
            assembly,
            executable,
            destination);

        Assert.True(plan.UsesSharedManagerRuntime);
        Assert.Equal(Path.Combine(destination, "manager", UpdaterName + ".exe"), plan.ExecutablePath);
        Assert.Equal(Path.Combine(destination, "manager"), plan.WorkingDirectory);
        Assert.Null(plan.ManagedEntryAssemblyPath);
        SelfRelocator.RelocationDirectory copy = Assert.Single(plan.Directories);
        Assert.Equal(manager, copy.SourceDirectory);
        Assert.Equal(Path.Combine(destination, "manager"), copy.DestinationDirectory);

        SelfRelocator.StagePlan(plan);

        Assert.True(File.Exists(Path.Combine(destination, "manager", Path.GetRelativePath(manager, runtimeFile))));
        ProcessStartInfo startInfo = SelfRelocator.CreateStartInfo(
            plan,
            new[] { "apply", "--target", releaseRoot, "value with spaces" },
            redirectOutput: true);
        Assert.Equal(plan.ExecutablePath, startInfo.FileName);
        Assert.Equal(plan.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[] { "apply", "--target", releaseRoot, "value with spaces", "--staged-run" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void SharedManagerRuntime_RejectsEntryAssemblyOutsideManager()
    {
        using TestDirectory test = new();
        string manager = CreateSharedManagerLayout(test);
        string executable = Path.Combine(manager, UpdaterName + ".exe");
        string outsideAssembly = test.CreateFile("outside", UpdaterName + ".dll");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SelfRelocator.CreateRelocationPlan(
                manager,
                outsideAssembly,
                executable,
                Path.Combine(test.Root, "temporary", "copy")));

        Assert.Contains("入口程序集", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedManagerRuntime_RejectsProcessFromDifferentManagerDirectory()
    {
        using TestDirectory test = new();
        string manager = CreateSharedManagerLayout(test);
        string assembly = Path.Combine(manager, UpdaterName + ".dll");
        string differentExecutable = test.CreateFile("other-release", "manager", UpdaterName + ".exe");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SelfRelocator.CreateRelocationPlan(
                manager,
                assembly,
                differentExecutable,
                Path.Combine(test.Root, "temporary", "copy")));

        Assert.Contains("进程", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedManagerRuntime_RequiresRuntimeMetadata()
    {
        using TestDirectory test = new();
        string manager = test.CreateDirectory("release", "manager");
        string executable = test.CreateFile("release", "manager", UpdaterName + ".exe");
        string assembly = test.CreateFile("release", "manager", UpdaterName + ".dll");
        test.CreateFile("release", "manager", UpdaterName + ".deps.json");

        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(() =>
            SelfRelocator.CreateRelocationPlan(
                manager,
                assembly,
                executable,
                Path.Combine(test.Root, "temporary", "copy")));

        Assert.Contains("运行配置", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneAppHost_RemainsSupported()
    {
        using TestDirectory test = new();
        string updater = test.CreateDirectory("legacy-updater");
        string executable = test.CreateFile("legacy-updater", UpdaterName + ".exe");
        string assembly = test.CreateFile("legacy-updater", UpdaterName + ".dll");
        string destination = Path.Combine(test.Root, "temporary", "copy");

        SelfRelocator.RelocationPlan plan = SelfRelocator.CreateRelocationPlan(
            updater,
            assembly,
            executable,
            destination);

        Assert.False(plan.UsesSharedManagerRuntime);
        Assert.Equal(Path.Combine(destination, UpdaterName + ".exe"), plan.ExecutablePath);
        Assert.Null(plan.ManagedEntryAssemblyPath);
        Assert.Equal(destination, plan.WorkingDirectory);
        Assert.Single(plan.Directories);
    }

    [Fact]
    public void FrameworkDependentFallback_UsesExternalHostAndCopiedAssembly()
    {
        using TestDirectory test = new();
        string updater = test.CreateDirectory("development-updater");
        string assembly = test.CreateFile("development-updater", UpdaterName + ".dll");
        string host = test.CreateFile("sdk", "dotnet.exe");
        string destination = Path.Combine(test.Root, "temporary", "copy");

        SelfRelocator.RelocationPlan plan = SelfRelocator.CreateRelocationPlan(
            updater,
            assembly,
            host,
            destination);
        ProcessStartInfo startInfo = SelfRelocator.CreateStartInfo(
            plan,
            new[] { "apply", "--json" },
            redirectOutput: true);

        Assert.False(plan.UsesSharedManagerRuntime);
        Assert.Equal(host, plan.ExecutablePath);
        Assert.Equal(Path.Combine(destination, UpdaterName + ".dll"), plan.ManagedEntryAssemblyPath);
        Assert.Equal(
            new[]
            {
                Path.Combine(destination, UpdaterName + ".dll"),
                "apply",
                "--json",
                "--staged-run"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void DestinationInsideSource_IsRejectedBeforeCopying()
    {
        using TestDirectory test = new();
        string updater = test.CreateDirectory("legacy-updater");
        string executable = test.CreateFile("legacy-updater", UpdaterName + ".exe");
        string assembly = test.CreateFile("legacy-updater", UpdaterName + ".dll");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SelfRelocator.CreateRelocationPlan(
                updater,
                assembly,
                executable,
                Path.Combine(updater, "nested-copy")));

        Assert.Contains("不能重叠", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedManagerRuntime_RejectsReparsePointReleaseRootWhenSupported()
    {
        using TestDirectory test = new();
        string realRelease = test.CreateDirectory("real-release");
        string manager = test.CreateDirectory("real-release", "manager");
        string executable = test.CreateFile("real-release", "manager", UpdaterName + ".exe");
        string assembly = test.CreateFile("real-release", "manager", UpdaterName + ".dll");
        test.CreateFile("real-release", "manager", UpdaterName + ".deps.json");
        test.CreateFile("real-release", "manager", UpdaterName + ".runtimeconfig.json");
        string link = Path.Combine(test.Root, "linked-release");
        if (!TryCreateDirectoryLink(link, realRelease))
        {
            return;
        }

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                SelfRelocator.CreateRelocationPlan(
                    Path.Combine(link, "manager"),
                    Path.Combine(link, "manager", Path.GetFileName(assembly)),
                    Path.Combine(link, "manager", Path.GetFileName(executable)),
                    Path.Combine(test.Root, "temporary", "copy")));

            Assert.Contains("发布根目录", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static string CreateSharedManagerLayout(TestDirectory test)
    {
        string manager = test.CreateDirectory("release", "manager");
        test.CreateFile("release", "manager", UpdaterName + ".exe");
        test.CreateFile("release", "manager", UpdaterName + ".dll");
        test.CreateFile("release", "manager", UpdaterName + ".deps.json");
        test.CreateFile("release", "manager", UpdaterName + ".runtimeconfig.json");
        return manager;
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "Loopstructor-SelfRelocatorTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateDirectory(params string[] parts)
        {
            string path = Combine(parts);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(params string[] parts)
        {
            string path = Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string Combine(string[] parts) =>
            parts.Aggregate(Root, Path.Combine);
    }
}
