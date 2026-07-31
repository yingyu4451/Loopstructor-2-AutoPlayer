using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class TransactionalInstaller
{
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
        string backup = Path.Combine(
            targetParent,
            ".LoopstructorAutoPlayer-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + transactionId[..8]);
        UpdateTransactionJournal journal = new()
        {
            TransactionId = transactionId,
            TargetRoot = target,
            StagingRoot = staging,
            BackupRoot = backup,
            Version = expectedVersion,
            Phase = "prepared",
            UpdatedAtUtc = DateTime.UtcNow
        };
        WriteJournal(journal);
        ReportProgressSafely(progress, UpdateInstallPhase.Prepared);

        try
        {
            Directory.Move(target, backup);
            UpdatePhase(journal, "backup-created");
            ReportProgressSafely(progress, UpdateInstallPhase.BackupCreated);
            Directory.Move(staging, target);
            UpdatePhase(journal, "installed");
            ReportProgressSafely(progress, UpdateInstallPhase.Installed);
            _validator.Validate(target, expectedVersion, validateTargetSafety: true);
            ReportProgressSafely(progress, UpdateInstallPhase.Validated);
            UpdatePhase(journal, "complete");
            DeleteJournal();
            return backup;
        }
        catch (Exception applyError)
        {
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
            DeleteJournal();
            return "已恢复一项在事务日志完成前已经安装成功的更新。";
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
            string parent = Directory.GetParent(journal.TargetRoot)!.FullName;
            string failed = Path.Combine(parent, ".LoopstructorAutoPlayer-failed-" + journal.TransactionId[..8]);
            if (Directory.Exists(failed))
            {
                failed += "-" + Guid.NewGuid().ToString("N")[..6];
            }

            Directory.Move(journal.TargetRoot, failed);
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
        if (!string.Equals(Directory.GetParent(backup)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Directory.GetParent(staging)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(backup).StartsWith(".LoopstructorAutoPlayer-backup-", StringComparison.Ordinal)
            || !Path.GetFileName(staging).StartsWith(".LoopstructorAutoPlayer-staging-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新事务日志包含不安全的路径。");
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
