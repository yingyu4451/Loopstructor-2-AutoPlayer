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
                        ?? throw new InvalidOperationException("Update target has no parent directory.");
        return Path.Combine(parent, ".LoopstructorAutoPlayer-staging-" + Guid.NewGuid().ToString("N"));
    }

    public string Apply(string stagingRoot, string targetRoot, string expectedVersion)
    {
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        string staging = ReleasePackageValidator.NormalizeRoot(stagingRoot);
        _validator.Validate(target, validateTargetSafety: true);
        _validator.Validate(staging, expectedVersion);
        string targetParent = Directory.GetParent(target)?.FullName
                              ?? throw new InvalidOperationException("Update target has no parent directory.");
        string stagingParent = Directory.GetParent(staging)?.FullName
                               ?? throw new InvalidOperationException("Staging root has no parent directory.");
        if (!string.Equals(targetParent, stagingParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staging and target must share a parent directory for transactional replacement.");
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

        try
        {
            Directory.Move(target, backup);
            UpdatePhase(journal, "backup-created");
            Directory.Move(staging, target);
            UpdatePhase(journal, "installed");
            _validator.Validate(target, expectedVersion, validateTargetSafety: true);
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

            string message = "Update replacement failed: " + applyError.Message;
            if (rollbackError != null)
            {
                message += " Rollback also failed: " + rollbackError.Message + ". Recovery journal: " + JournalPath;
            }
            else
            {
                message += previousReleaseUnchanged
                    ? " The previous release was unchanged."
                    : " The previous release was restored.";
            }

            throw new IOException(message, applyError);
        }
    }

    public string RecoverIncomplete(string targetRoot)
    {
        if (!File.Exists(JournalPath)) return string.Empty;
        UpdateTransactionJournal journal = JsonSerializer.Deserialize<UpdateTransactionJournal>(
            File.ReadAllText(JournalPath),
            JsonOptions) ?? throw new InvalidDataException("Updater transaction journal is empty.");
        string target = ReleasePackageValidator.NormalizeRoot(targetRoot);
        if (!string.Equals(target, ReleasePackageValidator.NormalizeRoot(journal.TargetRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recovery journal belongs to a different release root.");
        }

        ValidateJournalPaths(journal);
        if (TryValidate(target, journal.Version))
        {
            DeleteJournal();
            return "Recovered an update that completed before its journal was finalized.";
        }

        if (Directory.Exists(journal.BackupRoot))
        {
            RestoreBackup(journal);
            _validator.Validate(target, validateTargetSafety: true);
            DeleteJournal();
            return "Restored the previous release from an interrupted update.";
        }

        if (Directory.Exists(target))
        {
            _validator.Validate(target, validateTargetSafety: true);
            DeleteJournal();
            return "Cleared an interrupted update before replacement began or after rollback completed.";
        }

        throw new IOException("Recovery journal exists, but neither a valid target nor backup is available: " + JournalPath);
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
            throw new DirectoryNotFoundException("Update backup is missing: " + journal.BackupRoot);
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
                        ?? throw new InvalidDataException("Journal target has no parent.");
        string backup = ReleasePackageValidator.NormalizeRoot(journal.BackupRoot);
        string staging = ReleasePackageValidator.NormalizeRoot(journal.StagingRoot);
        if (!string.Equals(Directory.GetParent(backup)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Directory.GetParent(staging)?.FullName, parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(backup).StartsWith(".LoopstructorAutoPlayer-backup-", StringComparison.Ordinal)
            || !Path.GetFileName(staging).StartsWith(".LoopstructorAutoPlayer-staging-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Updater transaction journal contains unsafe paths.");
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
            throw new IOException("Updater transaction journal could not be finalized.", exception);
        }
    }
}
