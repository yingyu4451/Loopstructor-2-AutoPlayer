using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class TransactionalInstaller
{
    private const string RollbackPrefix = ".LoopstructorAutoPlayer-rollback-";
    private const string LegacyBackupPrefix = ".LoopstructorAutoPlayer-backup-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ReleasePackageValidator _validator;

    public TransactionalInstaller(ReleasePackageValidator? validator = null, string? journalPath = null)
    {
        _validator = validator ?? new ReleasePackageValidator();
        JournalPath = journalPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoopstructorAutoPlayer",
            "updater",
            "transaction.json");
    }

    public string JournalPath { get; }

    public static string GetDefaultJournalPath(string targetRoot)
    {
        string normalized = ReleasePackageValidator.NormalizeRoot(targetRoot).ToUpperInvariant();
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..24];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoopstructorAutoPlayer",
            "updater",
            "transaction-" + identity + ".json");
    }

    public string CreateStagingRoot(string targetRoot)
    {
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        string parent = Directory.GetParent(target)?.FullName
                        ?? throw new InvalidOperationException("更新目标没有父目录。");
        return Path.Combine(parent, ".LoopstructorAutoPlayer-staging-" + Guid.NewGuid().ToString("N"));
    }

    public string Apply(
        string stagingRoot,
        string targetRoot,
        string expectedVersion,
        Action<UpdateInstallPhase>? progress = null)
    {
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        string staging = ReleasePackageValidator.NormalizeRoot(stagingRoot);
        _validator.Validate(target, validateTargetSafety: true);
        _validator.Validate(staging, expectedVersion);
        string targetParent = Directory.GetParent(target)?.FullName
                              ?? throw new InvalidOperationException("更新目标没有父目录。");
        string stagingParent = Directory.GetParent(staging)?.FullName
                               ?? throw new InvalidOperationException("暂存目录没有父目录。");
        if (!string.Equals(targetParent, stagingParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("暂存目录与更新目标必须具有同一个父目录，才能执行事务替换。");
        }

        PreserveConfiguration(target, staging);
        string transactionId = Guid.NewGuid().ToString("N");
        string rollback = Path.Combine(
            targetParent,
            RollbackPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + transactionId[..8]);
        UpdateTransactionJournal journal = new()
        {
            TransactionId = transactionId,
            TargetRoot = target,
            StagingRoot = staging,
            BackupRoot = rollback,
            Version = expectedVersion,
            Phase = "prepared",
            UpdatedAtUtc = DateTime.UtcNow
        };
        WriteJournal(journal);
        ReportProgressSafely(progress, UpdateInstallPhase.Prepared);

        bool newReleaseValidated = false;
        try
        {
            Directory.Move(target, rollback);
            UpdatePhase(journal, "rollback-created");
            ReportProgressSafely(progress, UpdateInstallPhase.BackupCreated);
            Directory.Move(staging, target);
            UpdatePhase(journal, "installed");
            ReportProgressSafely(progress, UpdateInstallPhase.Installed);
            _validator.Validate(target, expectedVersion, validateTargetSafety: true);
            newReleaseValidated = true;
            ReportProgressSafely(progress, UpdateInstallPhase.Validated);
            CompleteCommittedTransaction(journal);
            return string.Empty;
        }
        catch (Exception applyError)
        {
            if (newReleaseValidated)
            {
                TryMarkCleanupPending(journal);
                return string.Empty;
            }

            Exception? rollbackError = null;
            bool previousReleaseUnchanged = false;
            try
            {
                if (Directory.Exists(journal.BackupRoot))
                {
                    RestoreBackup(journal);
                }
                else
                {
                    _validator.Validate(target, validateTargetSafety: true);
                    previousReleaseUnchanged = true;
                }

                UpdatePhase(journal, "rolled-back");
                _validator.Validate(target, validateTargetSafety: true);
                DeleteJournal();
            }
            catch (Exception exception)
            {
                rollbackError = exception;
                UpdatePhase(journal, "rollback-failed");
            }

            string message = "替换更新文件失败（" + DescribeFileOperationFailure(applyError) + "）。";
            if (rollbackError != null)
            {
                message += " 回滚也失败（" + DescribeFileOperationFailure(rollbackError) + "）。恢复日志：" + JournalPath;
            }
            else
            {
                message += previousReleaseUnchanged
                    ? " 上一版本未发生变化。"
                    : " 已恢复上一版本。";
            }

            throw new IOException(message, applyError);
        }
    }

    private static void ReportProgressSafely(Action<UpdateInstallPhase>? progress, UpdateInstallPhase phase)
    {
        try
        {
            progress?.Invoke(phase);
        }
        catch
        {
            // Progress display is non-authoritative and must never interrupt the update transaction.
        }
    }

    private static string DescribeFileOperationFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "没有文件访问权限",
        FileNotFoundException => "所需文件不存在",
        DirectoryNotFoundException => "所需目录不存在",
        InvalidDataException => "安装文件校验失败",
        IOException => "文件被占用或无法移动",
        _ => "发生未预期错误，类型为 " + exception.GetType().Name
    };

    public string RecoverIncomplete(string targetRoot)
    {
        if (!File.Exists(JournalPath)) return string.Empty;
        UpdateTransactionJournal journal = JsonSerializer.Deserialize<UpdateTransactionJournal>(
            File.ReadAllText(JournalPath),
            JsonOptions) ?? throw new InvalidDataException("更新事务日志为空。");
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        if (!string.Equals(target, ReleasePackageValidator.NormalizeRoot(journal.TargetRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("恢复日志属于另一个发布目录。");
        }

        ValidateJournalPaths(journal);
        if (TryValidate(target, journal.Version))
        {
            if (Directory.Exists(journal.BackupRoot) && !TryDeleteDirectory(journal.BackupRoot))
            {
                UpdatePhase(journal, "cleanup-pending");
                throw new IOException("新版已经安装成功，但临时回滚目录尚未清理完成。请重新运行更新器重试清理。");
            }

            CleanupLegacyBackups(target);
            DeleteJournal();
            return "已完成一项在事务日志结束前已经安装成功的更新，并清理临时回滚目录。";
        }

        if (Directory.Exists(journal.BackupRoot))
        {
            RestoreBackup(journal);
            _validator.Validate(target, validateTargetSafety: true);
            DeleteJournal();
            return "已从中断的更新中恢复上一版本。";
        }

        if (Directory.Exists(target))
        {
            _validator.Validate(target, validateTargetSafety: true);
            DeleteJournal();
            return "已清理在替换开始前或回滚完成后中断的更新。";
        }

        throw new IOException("存在恢复日志，但找不到有效的更新目标或备份：" + JournalPath);
    }

    private static void PreserveConfiguration(string target, string staging)
    {
        string source = Path.Combine(target, "autoplayer-update.json");
        string destination = Path.Combine(staging, "autoplayer-update.json");
        if (File.Exists(source) && !File.Exists(destination))
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    private void RestoreBackup(UpdateTransactionJournal journal)
    {
        if (!Directory.Exists(journal.BackupRoot))
        {
            throw new DirectoryNotFoundException("更新备份不存在：" + journal.BackupRoot);
        }

        if (Directory.Exists(journal.TargetRoot))
        {
            Directory.Delete(journal.TargetRoot, recursive: true);
        }

        Directory.Move(journal.BackupRoot, journal.TargetRoot);
    }

    private static void ValidateJournalPaths(UpdateTransactionJournal journal)
    {
        string target = ReleasePackageValidator.NormalizeRoot(journal.TargetRoot);
        string parent = Directory.GetParent(target)?.FullName
                        ?? throw new InvalidDataException("事务日志中的目标没有父目录。");
        string backup = ReleasePackageValidator.NormalizeRoot(journal.BackupRoot);
        string staging = ReleasePackageValidator.NormalizeRoot(journal.StagingRoot);
        string backupName = Path.GetFileName(backup);
        if (!string.Equals(Directory.GetParent(backup)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Directory.GetParent(staging)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || (!IsGeneratedTransactionDirectoryName(backupName, RollbackPrefix)
                && !IsGeneratedTransactionDirectoryName(backupName, LegacyBackupPrefix))
            || !Path.GetFileName(staging).StartsWith(".LoopstructorAutoPlayer-staging-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新事务日志包含不安全的路径。");
        }
    }

    private void CompleteCommittedTransaction(UpdateTransactionJournal journal)
    {
        UpdatePhase(journal, "validated");
        if (!TryDeleteDirectory(journal.BackupRoot))
        {
            UpdatePhase(journal, "cleanup-pending");
            return;
        }

        CleanupLegacyBackups(journal.TargetRoot);
        UpdatePhase(journal, "complete");
        DeleteJournal();
    }

    private void CleanupLegacyBackups(string targetRoot)
    {
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        string parent = Directory.GetParent(target)?.FullName
                        ?? throw new InvalidOperationException("更新目标没有父目录。");
        foreach (string candidate in Directory.EnumerateDirectories(
                     parent,
                     LegacyBackupPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                string full = ReleasePackageValidator.NormalizeRoot(candidate);
                if (!string.Equals(Directory.GetParent(full)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
                    || !IsGeneratedTransactionDirectoryName(Path.GetFileName(full), LegacyBackupPrefix))
                {
                    continue;
                }

                _validator.Validate(full, validateTargetSafety: true);
                _ = TryDeleteDirectory(full);
            }
            catch
            {
                // Only updater-authored, structurally valid legacy backups are eligible for cleanup.
            }
        }
    }

    private static bool IsGeneratedTransactionDirectoryName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string suffix = name[prefix.Length..];
        if (suffix.Length != 24 || suffix[8] != '-' || suffix[15] != '-') return false;
        return DateTime.TryParseExact(
                   suffix[..15],
                   "yyyyMMdd-HHmmss",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _) &&
               suffix[16..].All(character => Uri.IsHexDigit(character));
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return true;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        return !Directory.Exists(path);
    }

    private void TryMarkCleanupPending(UpdateTransactionJournal journal)
    {
        try
        {
            UpdatePhase(journal, "cleanup-pending");
        }
        catch
        {
            // The validated release remains authoritative even when cleanup journaling fails.
        }
    }

    private bool TryValidate(string root, string expectedVersion)
    {
        try
        {
            _validator.Validate(root, expectedVersion, validateTargetSafety: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdatePhase(UpdateTransactionJournal journal, string phase)
    {
        journal.Phase = phase;
        journal.UpdatedAtUtc = DateTime.UtcNow;
        WriteJournal(journal);
    }

    private void WriteJournal(UpdateTransactionJournal journal)
    {
        UpdaterAtomicFile.WriteAllText(JournalPath, JsonSerializer.Serialize(journal, JsonOptions));
    }

    private void DeleteJournal()
    {
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
        }
        catch (Exception exception)
        {
            throw new IOException("无法完成更新事务日志。", exception);
        }
    }
}
