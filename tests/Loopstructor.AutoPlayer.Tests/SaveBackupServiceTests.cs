using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Loopstructor.AutoPlayer.Manager.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class SaveBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loopstructor-save-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResidentPlayer_CreatesChapterLevelDatedSnapshots_AndAppliesRetention()
    {
        string source = Path.Combine(_root, "game-save");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "progress.json"), "one");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 2 };

        await ObserveStep(service, settings, source, chapter: 1, level: 2);
        File.WriteAllText(Path.Combine(source, "progress.json"), "two");
        await ObserveStep(service, settings, source, chapter: 1, level: 3);
        File.WriteAllText(Path.Combine(source, "progress.json"), "three");
        await ObserveStep(service, settings, source, chapter: 2, level: 1);

        SaveBackupStatus snapshot = service.Snapshot(true, 2);
        Assert.Equal(2, snapshot.BackupCount);
        DirectoryInfo[] backups = new DirectoryInfo(snapshot.BackupRoot).EnumerateDirectories()
            .Where(directory => !directory.Name.StartsWith(".pending-", StringComparison.Ordinal))
            .OrderBy(directory => directory.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.DoesNotContain(backups, directory => directory.Name.StartsWith("第01章-第002关-", StringComparison.Ordinal));
        Assert.Contains(backups, directory => directory.Name.StartsWith("第01章-第003关-", StringComparison.Ordinal));
        DirectoryInfo latest = Assert.Single(backups, directory => directory.Name.StartsWith("第02章-第001关-", StringComparison.Ordinal));
        Assert.Equal("three", File.ReadAllText(Path.Combine(latest.FullName, "progress.json")));
    }

    [Fact]
    public async Task IsolatedQaStatus_DoesNotBackUpTestProfile()
    {
        string source = Path.Combine(_root, "qa-save");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "progress.json"), "qa");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 10 };
        AutoPlayerStatus status = Status(source, 1, 1);
        status.ActivationMode = AutoPlayerActivationMode.IsolatedQa;

        Assert.Null(await service.ObserveAsync(status, settings, _root, CancellationToken.None));
        Assert.Equal(0, service.Snapshot(true, 10).BackupCount);
    }

    [Fact]
    public async Task SameStep_MetadataOnlyChanges_DoNotCreateDuplicateSnapshot()
    {
        string source = Path.Combine(_root, "game-save");
        Directory.CreateDirectory(source);
        string save = Path.Combine(source, "progress.json");
        File.WriteAllText(save, "stable");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 10 };

        await ObserveStep(service, settings, source, chapter: 1, level: 2);
        File.SetLastWriteTimeUtc(save, DateTime.UtcNow.AddMinutes(1));
        AutoPlayerStatus sameStep = Status(source, 1, 2);
        Assert.Null(await service.ObserveAsync(sameStep, settings, Path.GetDirectoryName(source), CancellationToken.None));

        Assert.Equal(1, service.Snapshot(true, 10).BackupCount);
    }

    [Fact]
    public async Task SameStep_ContentChange_CreatesOneAdditionalSnapshot()
    {
        string source = Path.Combine(_root, "game-save");
        Directory.CreateDirectory(source);
        string save = Path.Combine(source, "progress.json");
        File.WriteAllText(save, "one");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 10 };

        await ObserveStep(service, settings, source, chapter: 1, level: 2);
        File.WriteAllText(save, "two-two");
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        Assert.NotNull(await service.ObserveAsync(Status(source, 1, 2), settings, Path.GetDirectoryName(source), CancellationToken.None));

        Assert.Equal(2, service.Snapshot(true, 10).BackupCount);
    }

    private static async Task ObserveStep(
        SaveBackupService service,
        ManagerSettings settings,
        string source,
        int chapter,
        int level)
    {
        AutoPlayerStatus status = Status(source, chapter, level);
        Assert.Null(await service.ObserveAsync(status, settings, Path.GetDirectoryName(source), CancellationToken.None));
        await service.ObserveAsync(status, settings, Path.GetDirectoryName(source), CancellationToken.None);
    }

    private static AutoPlayerStatus Status(string source, int chapter, int level) => new()
    {
        ActivationMode = AutoPlayerActivationMode.ResidentPlayer,
        ActiveSaveRoot = source,
        CurrentMapStage = chapter - 1,
        CurrentMapLayer = level
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
