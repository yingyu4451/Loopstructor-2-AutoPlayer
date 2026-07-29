using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

string verificationRoot = Path.Combine(Path.GetTempPath(), "LoopstructorUpdaterVerification-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(verificationRoot);
try
{
    VerifySemanticVersions();
    VerifyZipExtraction(verificationRoot);
    VerifyTransactionalReplacement(verificationRoot);
    VerifyInterruptedRecovery(verificationRoot);
    VerifyPreparedWindowRecovery(verificationRoot);
    VerifyInvalidInstallRollback(verificationRoot);
    VerifyTargetLock(verificationRoot);
    Console.WriteLine("Updater verification passed.");
}
finally
{
    if (Directory.Exists(verificationRoot)) Directory.Delete(verificationRoot, recursive: true);
}

static void VerifySemanticVersions()
{
    Require(SemanticVersion.TryParse("1.0.0-beta.1", out SemanticVersion? preview), "preview SemVer parse");
    Require(SemanticVersion.TryParse("1.0.0", out SemanticVersion? stable), "stable SemVer parse");
    Require(preview!.CompareTo(stable) < 0, "SemVer prerelease precedence");
}

static void VerifyZipExtraction(string root)
{
    SecureZipExtractor extractor = new();
    string validZip = Path.Combine(root, "valid.zip");
    using (ZipArchive archive = ZipFile.Open(validZip, ZipArchiveMode.Create))
    {
        ZipArchiveEntry entry = archive.CreateEntry("folder/file.txt");
        using StreamWriter writer = new(entry.Open());
        writer.Write("verified");
    }

    string validDestination = Path.Combine(root, "valid-extracted");
    extractor.Extract(validZip, validDestination);
    Require(File.ReadAllText(Path.Combine(validDestination, "folder", "file.txt")) == "verified", "valid ZIP extraction");

    string traversalZip = Path.Combine(root, "traversal.zip");
    using (ZipArchive archive = ZipFile.Open(traversalZip, ZipArchiveMode.Create))
    {
        ZipArchiveEntry entry = archive.CreateEntry("../escape.txt");
        using StreamWriter writer = new(entry.Open());
        writer.Write("blocked");
    }

    ExpectInvalidData(() => extractor.Extract(traversalZip, Path.Combine(root, "traversal-extracted")), "ZIP traversal rejection");
    Require(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal did not write outside staging");

    string linkZip = Path.Combine(root, "link.zip");
    using (ZipArchive archive = ZipFile.Open(linkZip, ZipArchiveMode.Create))
    {
        ZipArchiveEntry entry = archive.CreateEntry("link");
        entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        using StreamWriter writer = new(entry.Open());
        writer.Write("target");
    }

    ExpectInvalidData(() => extractor.Extract(linkZip, Path.Combine(root, "link-extracted")), "ZIP symlink rejection");
}

static void VerifyTransactionalReplacement(string root)
{
    string target = Path.Combine(root, "release");
    CreateRelease(target, "0.1.0");
    File.WriteAllText(Path.Combine(target, "autoplayer-update.json"), "{\"githubOwner\":\"configured\"}");
    TransactionalInstaller installer = new(journalPath: Path.Combine(root, "transaction.json"));
    string staging = installer.CreateStagingRoot(target);
    CreateRelease(staging, "0.2.0");
    string backup = installer.Apply(staging, target, "0.2.0");
    Require(ReadReleaseVersion(target) == "0.2.0", "new release moved into target");
    Require(ReadReleaseVersion(backup) == "0.1.0", "previous release retained as backup");
    Require(File.Exists(Path.Combine(target, "autoplayer-update.json")), "update configuration preserved");
    Require(!File.Exists(installer.JournalPath), "completed transaction journal removed");
}

static void VerifyInterruptedRecovery(string root)
{
    string target = Path.Combine(root, "recovery-release");
    CreateRelease(target, "0.3.0");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-backup-recovery");
    string staging = Path.Combine(root, ".LoopstructorAutoPlayer-staging-recovery");
    Directory.Move(target, backup);
    Directory.CreateDirectory(staging);
    string journalPath = Path.Combine(root, "recovery-transaction.json");
    UpdateTransactionJournal journal = new()
    {
        TransactionId = "12345678abcdef00",
        TargetRoot = target,
        BackupRoot = backup,
        StagingRoot = staging,
        Version = "0.4.0",
        Phase = "backup-created",
        UpdatedAtUtc = DateTime.UtcNow
    };
    File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
    TransactionalInstaller installer = new(journalPath: journalPath);
    string message = installer.RecoverIncomplete(target);
    Require(message.Contains("Restored", StringComparison.OrdinalIgnoreCase), "interrupted transaction reports restore");
    Require(ReadReleaseVersion(target) == "0.3.0", "interrupted transaction restored old target");
    Require(!File.Exists(journalPath), "recovery journal removed");
}

static void VerifyInvalidInstallRollback(string root)
{
    string target = Path.Combine(root, "invalid-release");
    CreateRelease(target, "0.5.0");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-backup-invalid");
    string staging = Path.Combine(root, ".LoopstructorAutoPlayer-staging-invalid");
    Directory.Move(target, backup);
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "autoplayer-release.json"), "{\"version\":\"broken\"}");
    Directory.CreateDirectory(staging);
    string journalPath = Path.Combine(root, "invalid-transaction.json");
    UpdateTransactionJournal journal = new()
    {
        TransactionId = "abcdef0012345678",
        TargetRoot = target,
        BackupRoot = backup,
        StagingRoot = staging,
        Version = "0.6.0",
        Phase = "installed",
        UpdatedAtUtc = DateTime.UtcNow
    };
    File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
    TransactionalInstaller installer = new(journalPath: journalPath);
    installer.RecoverIncomplete(target);
    Require(ReadReleaseVersion(target) == "0.5.0", "invalid installed release rolled back");
    Require(!File.Exists(journalPath), "rollback journal removed");
    Require(Directory.GetDirectories(root, ".LoopstructorAutoPlayer-failed-*", SearchOption.TopDirectoryOnly).Length > 0,
        "invalid installed release retained for diagnostics");
}

static void VerifyPreparedWindowRecovery(string root)
{
    string target = Path.Combine(root, "prepared-release");
    CreateRelease(target, "0.7.0");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-backup-prepared");
    string staging = Path.Combine(root, ".LoopstructorAutoPlayer-staging-prepared");
    Directory.Move(target, backup);
    CreateRelease(staging, "0.8.0");
    string journalPath = Path.Combine(root, "prepared-transaction.json");
    File.WriteAllText(journalPath, JsonSerializer.Serialize(new UpdateTransactionJournal
    {
        TransactionId = "fedcba9876543210",
        TargetRoot = target,
        BackupRoot = backup,
        StagingRoot = staging,
        Version = "0.8.0",
        Phase = "prepared",
        UpdatedAtUtc = DateTime.UtcNow
    }));

    TransactionalInstaller installer = new(journalPath: journalPath);
    installer.RecoverIncomplete(target);
    Require(ReadReleaseVersion(target) == "0.7.0", "prepared crash window restored old target");
    Require(!File.Exists(journalPath), "prepared crash journal removed");
}

static void VerifyTargetLock(string root)
{
    string target = Path.Combine(root, "lock-release");
    Directory.CreateDirectory(target);
    using UpdateTargetLock first = UpdateTargetLock.Acquire(target, TimeSpan.FromSeconds(1));
    bool rejected = false;
    try
    {
        using UpdateTargetLock second = UpdateTargetLock.Acquire(target, TimeSpan.FromMilliseconds(300));
    }
    catch (IOException)
    {
        rejected = true;
    }

    Require(rejected, "concurrent updater target lock");
    string otherTarget = Path.Combine(root, "other-lock-release");
    Directory.CreateDirectory(otherTarget);
    using UpdateTargetLock independent = UpdateTargetLock.Acquire(otherTarget, TimeSpan.FromSeconds(1));
    Require(
        !string.Equals(first.Path, independent.Path, StringComparison.OrdinalIgnoreCase),
        "target-scoped updater locks");
    Require(
        !string.Equals(
            TransactionalInstaller.GetDefaultJournalPath(target),
            TransactionalInstaller.GetDefaultJournalPath(otherTarget),
            StringComparison.OrdinalIgnoreCase),
        "target-scoped updater journals");
}

static void CreateRelease(string root, string version)
{
    Directory.CreateDirectory(Path.Combine(root, "manager"));
    Directory.CreateDirectory(Path.Combine(root, "updater"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "bepinex"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "plugin"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "bepinex", "BepInEx", "core"));
    File.WriteAllText(Path.Combine(root, "manager", "Loopstructor.AutoPlayer.Manager.dll"), version);
    File.WriteAllText(Path.Combine(root, "updater", "Loopstructor.AutoPlayer.Updater.dll"), version);
    File.WriteAllText(Path.Combine(root, "payload", "bepinex", "winhttp.dll"), "x64-loader");
    File.WriteAllText(Path.Combine(root, "payload", "bepinex", "doorstop_config.ini"), "enabled=true");
    File.WriteAllText(Path.Combine(root, "payload", "bepinex", "BepInEx", "core", "BepInEx.dll"), "5.4.23.5");
    File.WriteAllText(Path.Combine(root, "payload", "bepinex", "BepInEx", "core", "BepInEx.Preloader.dll"), "5.4.23.5");
    File.WriteAllText(Path.Combine(root, "payload", "plugin", "Loopstructor.AutoPlayer.Plugin.dll"), version);
    File.WriteAllText(Path.Combine(root, "payload", "plugin", "Loopstructor.AutoPlayer.Core.dll"), version);
    File.WriteAllText(
        Path.Combine(root, "autoplayer-release.json"),
        JsonSerializer.Serialize(new ReleaseMarker
        {
            Version = version,
            BepInExVersion = "5.4.23.5",
            BepInExPayloadPath = "payload/bepinex",
            PluginPayloadPath = "payload/plugin"
        }));
    WriteChecksums(root);
}

static void WriteChecksums(string root)
{
    string[] lines = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !string.Equals(Path.GetFileName(path), "checksums.sha256", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path =>
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha256 = SHA256.Create();
            string hash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            return hash + "  " + Path.GetRelativePath(root, path).Replace('\\', '/');
        })
        .ToArray();
    File.WriteAllText(Path.Combine(root, "checksums.sha256"), string.Join('\n', lines) + "\n");
}

static string ReadReleaseVersion(string root)
{
    ReleaseMarker marker = JsonSerializer.Deserialize<ReleaseMarker>(File.ReadAllText(Path.Combine(root, "autoplayer-release.json")))!;
    return marker.Version;
}

static void ExpectInvalidData(Action action, string name)
{
    try
    {
        action();
        throw new InvalidOperationException(name + " did not reject the fixture.");
    }
    catch (InvalidDataException)
    {
        // Expected.
    }
}

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Verification failed: " + name);
}
