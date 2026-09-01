using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class SaveBackupService
{
    private static readonly Regex ManagedBackupName = new(
        @"^第\d{2,}章-第\d{3,}关-\d{8}-\d{6}(?:-\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ManagedBackupParts = new(
        @"^第(?<chapter>\d{2,})章-第(?<level>\d{3,})关-(?<date>\d{8})-(?<time>\d{6})(?:-\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(2);
    private const string SourceMetadataName = ".save-backup-source.json";
    private const int MaximumFileCount = 5_000;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _baseRoot;
    private readonly TimeSpan _settleDelay;
    private string _backupRoot = string.Empty;
    private string _observedStep = string.Empty;
    private string _lastObservedFingerprint = string.Empty;
    private string _pendingStep = string.Empty;
    private string _pendingSource = string.Empty;
    private DateTime _pendingDueUtc;
    private DateTime _nextContentProbeUtc;
    private string _latestBackup = string.Empty;
    private string _lastMessage = "尚未创建自动备份。";
    private DateTime? _lastBackupUtc;
    private bool _busy;
    private int _backupCount;
    private string _activeSaveRoot = string.Empty;

    public SaveBackupService(string baseRoot, TimeSpan? settleDelay = null)
    {
        _baseRoot = Path.GetFullPath(baseRoot ?? throw new ArgumentNullException(nameof(baseRoot)));
        _settleDelay = settleDelay ?? DefaultSettleDelay;
    }

    public SaveBackupStatus Snapshot(bool enabled, int maximumBackups) => new()
    {
        Enabled = enabled,
        MaximumBackups = Math.Clamp(maximumBackups, 1, 100),
        BackupCount = _backupCount,
        BackupRoot = _backupRoot,
        LatestBackup = _latestBackup,
        LastMessage = _lastMessage,
        LastBackupUtc = _lastBackupUtc,
        Pending = !string.IsNullOrEmpty(_pendingStep),
        Busy = _busy
    };

    public string EnsureBackupRoot(string? gameRoot)
    {
        string scope = string.IsNullOrWhiteSpace(gameRoot)
            ? string.Empty
            : Protocol.HashGameRoot(gameRoot).Substring(0, 16);
        _backupRoot = string.IsNullOrEmpty(scope) ? _baseRoot : Path.Combine(_baseRoot, scope);
        Directory.CreateDirectory(_backupRoot);
        RefreshCount();
        return _backupRoot;
    }

    public IReadOnlyList<SaveBackupEntry> ListBackups(string? gameRoot, CancellationToken cancellationToken = default)
    {
        string backupRoot = EnsureBackupRoot(gameRoot);
        DirectoryInfo root = new(backupRoot);
        if (!root.Exists) return Array.Empty<SaveBackupEntry>();

        List<SaveBackupEntry> entries = new();
        foreach (DirectoryInfo directory in EnumerateManagedBackups(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SourceFile> files = CaptureSourceFiles(directory.FullName, cancellationToken);
            Match match = ManagedBackupParts.Match(directory.Name);
            DateTime created = ParseCreatedAt(match, directory.CreationTime);
            entries.Add(new SaveBackupEntry
            {
                Id = directory.Name,
                Chapter = ParsePart(match, "chapter"),
                Level = ParsePart(match, "level"),
                CreatedAt = created,
                FileCount = files.Count,
                TotalBytes = files.Sum(file => file.Length),
                IsLatest = SamePath(directory.FullName, _latestBackup)
            });
        }
        return entries;
    }

    public SaveRestorePlan CreateRestorePlan(string? gameRoot, string backupId, string? activeSaveRoot)
    {
        string backupRoot = EnsureBackupRoot(gameRoot);
        string normalizedId = backupId?.Trim() ?? string.Empty;
        if (!ManagedBackupName.IsMatch(normalizedId)
            || !string.Equals(Path.GetFileName(normalizedId), normalizedId, StringComparison.Ordinal))
            throw new InvalidOperationException("所选存档不属于 AutoPlayer 管理的备份。");

        string backupDirectory = Path.GetFullPath(Path.Combine(backupRoot, normalizedId));
        if (!IsDirectChild(backupRoot, backupDirectory)
            || !Directory.Exists(backupDirectory)
            || new DirectoryInfo(backupDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("所选存档不存在或目录状态不安全。");

        string target = ResolveActiveSaveRoot(backupRoot, activeSaveRoot);
        ValidateRestoreTarget(target, backupRoot);
        IReadOnlyList<SourceFile> files = CaptureSourceFiles(backupDirectory, CancellationToken.None);
        if (files.Count == 0) throw new InvalidOperationException("所选备份为空，不能用于读档。");
        return new SaveRestorePlan(normalizedId, backupDirectory, target);
    }

    public async Task<SaveRestoreResult> RestoreAsync(SaveRestorePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_busy) throw new InvalidOperationException("存档服务正在执行另一项操作，请稍后重试。");
        _busy = true;
        _lastMessage = $"正在恢复 {plan.BackupId}。";
        try
        {
            SaveRestoreResult result = await Task.Run(
                () => RestoreTransactional(plan, cancellationToken),
                cancellationToken);
            _observedStep = string.Empty;
            _lastObservedFingerprint = string.Empty;
            _pendingStep = string.Empty;
            _nextContentProbeUtc = DateTime.UtcNow.AddSeconds(3);
            _lastMessage = result.Message;
            return result;
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<string?> ObserveAsync(
        AutoPlayerStatus? status,
        ManagerSettings settings,
        string? gameRoot,
        CancellationToken cancellationToken)
    {
        int maximumBackups = Math.Clamp(settings.MaximumSaveBackups, 1, 100);
        if (!settings.AutomaticSaveBackupEnabled)
        {
            _pendingStep = string.Empty;
            _lastMessage = "自动备份已关闭。";
            return null;
        }

        string backupRoot = EnsureBackupRoot(gameRoot);
        ApplyRetention(backupRoot, maximumBackups);
        if (status == null
            || status.ActivationMode != AutoPlayerActivationMode.ResidentPlayer
            || status.CurrentChapter <= 0
            || status.CurrentMapLayer < 0
            || string.IsNullOrWhiteSpace(status.ActiveSaveRoot))
        {
            _observedStep = string.Empty;
            _lastObservedFingerprint = string.Empty;
            _pendingStep = string.Empty;
            return null;
        }

        string source;
        try
        {
            source = Path.GetFullPath(status.ActiveSaveRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            _lastMessage = "游戏返回的存档目录无效，尚未备份。";
            return null;
        }
        if (!Directory.Exists(source) || PathsOverlap(source, backupRoot))
        {
            _lastMessage = "存档目录尚未就绪或与备份目录重叠，正在等待。";
            return null;
        }
        RememberSourceRoot(backupRoot, source);

        string step = status.CurrentChapter.ToString("D2") + ":" + status.CurrentMapLayer.ToString("D3");
        if (!string.Equals(_observedStep, step, StringComparison.Ordinal))
        {
            _observedStep = step;
            _lastObservedFingerprint = string.Empty;
            _pendingStep = step;
            _pendingSource = source;
            _pendingDueUtc = DateTime.UtcNow + _settleDelay;
            _nextContentProbeUtc = DateTime.MinValue;
            _lastMessage = $"检测到第 {status.CurrentChapter} 章、第 {status.CurrentMapLayer} 关，等待存档写入稳定。";
            return null;
        }
        bool periodicProbe = string.IsNullOrEmpty(_pendingStep)
                             && DateTime.UtcNow >= _nextContentProbeUtc;
        if (periodicProbe)
        {
            _pendingStep = step;
            _pendingSource = source;
            _pendingDueUtc = DateTime.UtcNow;
        }
        if (!string.Equals(_pendingStep, step, StringComparison.Ordinal) || _busy)
        {
            return null;
        }
        if (DateTime.UtcNow < _pendingDueUtc) return null;

        _busy = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chapter = status.CurrentChapter;
            int level = status.CurrentMapLayer;
            string? created = await Task.Run(
                () => CreateStableSnapshot(_pendingSource, backupRoot, chapter, level, maximumBackups, cancellationToken),
                cancellationToken);
            _pendingStep = string.Empty;
            if (created == null)
            {
                _lastMessage = "这一章节步骤的存档内容没有变化，无需重复备份。";
                _nextContentProbeUtc = DateTime.UtcNow.AddSeconds(2);
                return null;
            }

            _latestBackup = created;
            _lastBackupUtc = DateTime.UtcNow;
            _lastMessage = $"已自动备份第 {chapter} 章、第 {level} 关。";
            _nextContentProbeUtc = DateTime.UtcNow.AddSeconds(2);
            RefreshCount();
            return _lastMessage;
        }
        catch (IOException exception)
        {
            _pendingDueUtc = DateTime.UtcNow + _settleDelay;
            _lastMessage = "存档仍在写入，稍后自动重试：" + exception.Message;
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _pendingStep = string.Empty;
            _lastMessage = "没有权限读取当前存档，自动备份未完成：" + exception.Message;
            return null;
        }
        finally
        {
            _busy = false;
        }
    }

    private string? CreateStableSnapshot(
        string source,
        string backupRoot,
        int chapter,
        int level,
        int maximumBackups,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SourceFile> before = CaptureSourceFiles(source, cancellationToken);
        if (before.Count == 0) throw new IOException("存档目录暂时为空。 ");
        string fingerprint = Fingerprint(before);
        if (string.Equals(_lastObservedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return null;
        SaveBackupIndex index = LoadIndex(backupRoot);
        string step = chapter.ToString("D2") + ":" + level.ToString("D3");
        if (string.Equals(index.Step, step, StringComparison.Ordinal)
            && string.Equals(index.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(index.DirectoryName)
            && Directory.Exists(Path.Combine(backupRoot, index.DirectoryName)))
        {
            _lastObservedFingerprint = fingerprint;
            _latestBackup = Path.Combine(backupRoot, index.DirectoryName);
            RefreshCount();
            return null;
        }

        string staging = Path.Combine(backupRoot, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (SourceFile file in before)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(staging, file.RelativePath);
                string? destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
                using FileStream input = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
            }

            IReadOnlyList<SourceFile> after = CaptureSourceFiles(source, cancellationToken);
            if (!string.Equals(fingerprint, Fingerprint(after), StringComparison.OrdinalIgnoreCase))
                throw new IOException("复制期间游戏仍在写入存档。 ");

            string prefix = $"第{chapter:D2}章-第{level:D3}关-{DateTime.Now:yyyyMMdd-HHmmss}";
            string finalName = prefix;
            for (int suffix = 1; Directory.Exists(Path.Combine(backupRoot, finalName)); suffix++)
                finalName = prefix + "-" + suffix.ToString("D2");
            string finalPath = Path.Combine(backupRoot, finalName);
            Directory.Move(staging, finalPath);
            staging = string.Empty;

            SaveIndex(backupRoot, new SaveBackupIndex
            {
                Step = step,
                Fingerprint = fingerprint,
                DirectoryName = finalName
            });
            _lastObservedFingerprint = fingerprint;
            ApplyRetention(backupRoot, maximumBackups);
            return finalPath;
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static IReadOnlyList<SourceFile> CaptureSourceFiles(string source, CancellationToken cancellationToken)
    {
        DirectoryInfo root = new(source);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("存档根目录不能是重解析点。 ");

        List<SourceFile> files = new();
        long totalBytes = 0;
        Queue<DirectoryInfo> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Dequeue();
            foreach (DirectoryInfo child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                pending.Enqueue(child);
            }
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files.Add(new SourceFile(
                    file.FullName,
                    Path.GetRelativePath(source, file.FullName),
                    file.Length,
                    file.LastWriteTimeUtc));
                totalBytes += file.Length;
                if (files.Count > MaximumFileCount || totalBytes > MaximumTotalBytes)
                    throw new IOException("存档目录规模异常，已停止备份。 ");
            }
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static SaveRestoreResult RestoreTransactional(SaveRestorePlan plan, CancellationToken cancellationToken)
    {
        string target = Path.GetFullPath(plan.TargetDirectory);
        string parent = Path.GetDirectoryName(target)
                        ?? throw new IOException("无法确定玩家存档的父目录。");
        string targetName = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
        string transactionId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, $".{targetName}.restore-stage-{transactionId}");
        string rollback = Path.Combine(parent, $".{targetName}.restore-rollback-{transactionId}");
        bool targetMoved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SourceFile> sourceFiles = CaptureSourceFiles(plan.BackupDirectory, cancellationToken);
            string expectedFingerprint = ContentFingerprint(sourceFiles, cancellationToken);
            Directory.CreateDirectory(staging);
            CopyFiles(sourceFiles, staging, cancellationToken);
            IReadOnlyList<SourceFile> stagedFiles = CaptureSourceFiles(staging, cancellationToken);
            if (!string.Equals(expectedFingerprint, ContentFingerprint(stagedFiles, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new IOException("临时存档校验失败，尚未替换玩家存档。");

            Directory.Move(target, rollback);
            targetMoved = true;
            Directory.Move(staging, target);
            staging = string.Empty;
            IReadOnlyList<SourceFile> restoredFiles = CaptureSourceFiles(target, cancellationToken);
            if (!string.Equals(expectedFingerprint, ContentFingerprint(restoredFiles, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复后的玩家存档校验失败。");

            Directory.Delete(rollback, true);
            rollback = string.Empty;
            return new SaveRestoreResult(true, plan.BackupId, target, $"已恢复 {plan.BackupId}，正在重新启动游戏。 ");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (targetMoved && Directory.Exists(rollback))
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(rollback, target);
                rollback = string.Empty;
            }
            throw new IOException("读档未完成，读档前的玩家存档已恢复：" + exception.Message, exception);
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging)) Directory.Delete(staging, true);
            if (!string.IsNullOrEmpty(rollback) && Directory.Exists(rollback) && !Directory.Exists(target))
                Directory.Move(rollback, target);
        }
    }

    private static void CopyFiles(IReadOnlyList<SourceFile> files, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (SourceFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(destinationRoot, file.RelativePath);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using FileStream input = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
        }
    }

    private static string ContentFingerprint(IReadOnlyList<SourceFile> files, CancellationToken cancellationToken)
    {
        using SHA256 treeHash = SHA256.Create();
        foreach (SourceFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] header = Encoding.UTF8.GetBytes(file.RelativePath.Replace('\\', '/') + "\0" + file.Length + "\n");
            treeHash.TransformBlock(header, 0, header.Length, null, 0);
            using FileStream stream = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] fileHash = SHA256.HashData(stream);
            treeHash.TransformBlock(fileHash, 0, fileHash.Length, null, 0);
        }
        treeHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(treeHash.Hash!).ToLowerInvariant();
    }

    private static string Fingerprint(IReadOnlyList<SourceFile> files)
    {
        using SHA256 sha = SHA256.Create();
        foreach (SourceFile file in files)
        {
            byte[] line = Encoding.UTF8.GetBytes(
                file.RelativePath.Replace('\\', '/') + "\0" + file.Length + "\n");
            sha.TransformBlock(line, 0, line.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private void ApplyRetention(string backupRoot, int maximumBackups)
    {
        DirectoryInfo root = new(backupRoot);
        DirectoryInfo[] managed = root.Exists
            ? root.EnumerateDirectories()
                .Where(directory => ManagedBackupName.IsMatch(directory.Name)
                                    && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(directory => directory.CreationTimeUtc)
                .ThenByDescending(directory => directory.Name, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<DirectoryInfo>();
        foreach (DirectoryInfo stale in managed.Skip(Math.Clamp(maximumBackups, 1, 100)))
            stale.Delete(true);
        RefreshCount();
    }

    private void RefreshCount()
    {
        if (string.IsNullOrWhiteSpace(_backupRoot) || !Directory.Exists(_backupRoot))
        {
            _backupCount = 0;
            return;
        }
        DirectoryInfo[] managed = new DirectoryInfo(_backupRoot).EnumerateDirectories()
            .Where(directory => ManagedBackupName.IsMatch(directory.Name)
                                && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(directory => directory.CreationTimeUtc)
            .ThenByDescending(directory => directory.Name, StringComparer.Ordinal)
            .ToArray();
        _backupCount = managed.Length;
        if (string.IsNullOrWhiteSpace(_latestBackup) && managed.Length > 0)
            _latestBackup = managed[0].FullName;
    }

    private static IEnumerable<DirectoryInfo> EnumerateManagedBackups(DirectoryInfo root) =>
        root.EnumerateDirectories()
            .Where(directory => ManagedBackupName.IsMatch(directory.Name)
                                && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(directory => directory.Name, StringComparer.Ordinal);

    private string ResolveActiveSaveRoot(string backupRoot, string? activeSaveRoot)
    {
        string candidate = string.IsNullOrWhiteSpace(activeSaveRoot) ? _activeSaveRoot : activeSaveRoot;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            SaveBackupSource metadata = LoadSourceMetadata(backupRoot);
            candidate = metadata.SourceRoot;
        }
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("尚未读取到当前玩家存档目录。请先启动一次游戏并等待 Manager 连接。 ");
        return Path.GetFullPath(candidate);
    }

    private void RememberSourceRoot(string backupRoot, string source)
    {
        string normalized = Path.GetFullPath(source);
        if (SamePath(_activeSaveRoot, normalized)) return;
        _activeSaveRoot = normalized;
        string path = Path.Combine(backupRoot, SourceMetadataName);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonConvert.SerializeObject(new SaveBackupSource { SourceRoot = _activeSaveRoot }, Formatting.Indented));
        File.Move(temporary, path, true);
    }

    private static SaveBackupSource LoadSourceMetadata(string backupRoot)
    {
        string path = Path.Combine(backupRoot, SourceMetadataName);
        try
        {
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<SaveBackupSource>(File.ReadAllText(path)) ?? new SaveBackupSource()
                : new SaveBackupSource();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SaveBackupSource();
        }
    }

    private static void ValidateRestoreTarget(string target, string backupRoot)
    {
        if (!Path.IsPathFullyQualified(target) || !Directory.Exists(target))
            throw new InvalidOperationException("当前玩家存档目录不存在，不能执行读档。");
        string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (string.Equals(trimmed, Path.GetPathRoot(trimmed)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || new DirectoryInfo(trimmed).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || PathsOverlap(trimmed, backupRoot))
            throw new InvalidOperationException("当前玩家存档目录不满足安全恢复条件。");
    }

    private static bool IsDirectChild(string root, string candidate) =>
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int ParsePart(Match match, string group) =>
        match.Success && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static DateTime ParseCreatedAt(Match match, DateTime fallback)
    {
        string value = match.Success ? match.Groups["date"].Value + match.Groups["time"].Value : string.Empty;
        return DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out DateTime parsed)
            ? parsed
            : fallback;
    }

    private static bool PathsOverlap(string source, string backupRoot)
    {
        string normalizedSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedBackup = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedSource.StartsWith(normalizedBackup, StringComparison.OrdinalIgnoreCase)
               || normalizedBackup.StartsWith(normalizedSource, StringComparison.OrdinalIgnoreCase);
    }

    private static SaveBackupIndex LoadIndex(string backupRoot)
    {
        string path = Path.Combine(backupRoot, ".save-backup-index.json");
        try
        {
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<SaveBackupIndex>(File.ReadAllText(path)) ?? new SaveBackupIndex()
                : new SaveBackupIndex();
        }
        catch (JsonException)
        {
            return new SaveBackupIndex();
        }
    }

    private static void SaveIndex(string backupRoot, SaveBackupIndex index)
    {
        string path = Path.Combine(backupRoot, ".save-backup-index.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonConvert.SerializeObject(index, Formatting.Indented));
        File.Move(temporary, path, true);
    }

    private sealed record SourceFile(string FullPath, string RelativePath, long Length, DateTime LastWriteTimeUtc);

    private sealed class SaveBackupIndex
    {
        public string Step { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string DirectoryName { get; set; } = string.Empty;
    }

    private sealed class SaveBackupSource
    {
        public string SourceRoot { get; set; } = string.Empty;
    }
}

public sealed class SaveBackupEntry
{
    public string Id { get; init; } = string.Empty;
    public int Chapter { get; init; }
    public int Level { get; init; }
    public DateTime CreatedAt { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public bool IsLatest { get; init; }
}

public sealed record SaveRestorePlan(string BackupId, string BackupDirectory, string TargetDirectory);

public sealed record SaveRestoreResult(bool Success, string BackupId, string TargetDirectory, string Message);

public sealed class SaveBackupStatus
{
    public bool Enabled { get; init; }
    public int MaximumBackups { get; init; }
    public int BackupCount { get; init; }
    public string BackupRoot { get; init; } = string.Empty;
    public string LatestBackup { get; init; } = string.Empty;
    public string LastMessage { get; init; } = string.Empty;
    public DateTime? LastBackupUtc { get; init; }
    public bool Pending { get; init; }
    public bool Busy { get; init; }
}
