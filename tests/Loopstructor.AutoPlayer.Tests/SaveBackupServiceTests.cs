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

    [Fact]
    public async Task ListBackups_ReturnsEveryManagedSnapshotNewestFirst()
    {
        string source = Path.Combine(_root, "game-save");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "progress.json"), "one");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 10 };

        await ObserveStep(service, settings, source, chapter: 1, level: 2);
        File.WriteAllText(Path.Combine(source, "progress.json"), "second-save");
        await ObserveStep(service, settings, source, chapter: 2, level: 4);

        IReadOnlyList<SaveBackupEntry> backups = service.ListBackups(_root);

        Assert.Equal(2, backups.Count);
        Assert.Equal((2, 4), (backups[0].Chapter, backups[0].Level));
        Assert.Equal((1, 2), (backups[1].Chapter, backups[1].Level));
        Assert.All(backups, backup => Assert.True(backup.FileCount > 0));
    }

    [Fact]
    public async Task Restore_ReplacesThePlayerSaveAndRemovesFilesNotInTheSnapshot()
    {
        string source = Path.Combine(_root, "game-save");
        Directory.CreateDirectory(source);
        string progress = Path.Combine(source, "progress.json");
        File.WriteAllText(progress, "saved-state");
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);
        ManagerSettings settings = new() { AutomaticSaveBackupEnabled = true, MaximumSaveBackups = 10 };
        await ObserveStep(service, settings, source, chapter: 3, level: 7);
        SaveBackupEntry backup = Assert.Single(service.ListBackups(_root));

        File.WriteAllText(progress, "current-state");
        File.WriteAllText(Path.Combine(source, "not-in-backup.json"), "remove-me");
        SaveRestorePlan plan = service.CreateRestorePlan(_root, backup.Id, source);
        SaveRestoreResult result = await service.RestoreAsync(plan, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("saved-state", File.ReadAllText(progress));
        Assert.False(File.Exists(Path.Combine(source, "not-in-backup.json")));
        Assert.Empty(Directory.GetDirectories(_root, ".game-save.restore-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void RestorePlan_RejectsPathsThatAreNotManagedBackupIds()
    {
        SaveBackupService service = new(Path.Combine(_root, "backups"), TimeSpan.Zero);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            service.CreateRestorePlan(_root, "..\\other-save", Path.Combine(_root, "game-save")));

        Assert.Contains("不属于", error.Message, StringComparison.Ordinal);
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
