using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Loopstructor.AutoPlayer.Core;
using Loopstructor.AutoPlayer.Manager.Models;
using Newtonsoft.Json;

namespace Loopstructor.AutoPlayer.Manager.Services;

public sealed class SaveBackupService
{
    private static readonly Regex ManagedBackupName = new(
        @"^第\d{2,}章-第\d{3,}关-\d{8}-\d{6}(?:-\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(2);
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
}

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
