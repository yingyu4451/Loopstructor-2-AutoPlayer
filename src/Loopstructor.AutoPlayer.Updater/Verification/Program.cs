using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

if (args.Length > 0)
{
    if (args.Length == 4
        && string.Equals(args[0], "--verify-release-package", StringComparison.Ordinal)
        && string.Equals(args[2], "--expected-version", StringComparison.Ordinal))
    {
        VerifyPackagedRelease(args[1], args[3]);
        Console.WriteLine("Packaged release verification passed.");
        return;
    }

    if (args.Length == 8
        && string.Equals(args[0], "--verify-delta-package", StringComparison.Ordinal)
        && string.Equals(args[2], "--base-package", StringComparison.Ordinal)
        && string.Equals(args[4], "--expected-base-version", StringComparison.Ordinal)
        && string.Equals(args[6], "--expected-version", StringComparison.Ordinal))
    {
        VerifyPackagedDelta(args[1], args[3], args[5], args[7]);
        Console.WriteLine("Packaged incremental release verification passed.");
        return;
    }

    throw new ArgumentException(
        "Usage: --verify-release-package <zip-path> --expected-version <version> " +
        "or --verify-delta-package <zip-path> --base-package <zip-path> " +
        "--expected-base-version <version> --expected-version <version>");
}

string verificationRoot = Path.Combine(Path.GetTempPath(), "LoopstructorUpdaterVerification-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(verificationRoot);
try
{
    VerifySemanticVersions();
    VerifyDefaultUpdateConfiguration(verificationRoot);
    VerifyLegacyManagerEntryPointRejected(verificationRoot);
    VerifyMissingEntryPointsRejected(verificationRoot);
    VerifyRetiredUpdaterDirectoryRejected(verificationRoot);
    VerifyManagerRestartEntryPoint(verificationRoot);
    VerifyManagerEntryPointTraversalRejected(verificationRoot);
    VerifyZipExtraction(verificationRoot);
    VerifyReleasePackageExtraction(verificationRoot);
    VerifyWrappedReleaseTransaction(verificationRoot);
    VerifyTransactionalReplacement(verificationRoot);
    VerifyCommittedRecoveryCleanup(verificationRoot);
    VerifyLegacyBackupCleanup(verificationRoot);
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

static void VerifyPackagedRelease(string archivePath, string expectedVersion)
{
    string archive = Path.GetFullPath(archivePath);
    if (!File.Exists(archive))
    {
        throw new FileNotFoundException("Packaged release ZIP was not found.", archive);
    }

    string extractionRoot = Path.Combine(
        Path.GetTempPath(),
        "LoopstructorPackagedReleaseVerification-" + Guid.NewGuid().ToString("N"));
    try
    {
        new SecureZipExtractor().ExtractReleasePackage(archive, extractionRoot);
        new ReleasePackageValidator().Validate(extractionRoot, expectedVersion);
        Require(
            !Directory.Exists(Path.Combine(extractionRoot, SecureZipExtractor.ReleaseArchiveRootDirectory)),
            "production release extraction strips the fixed archive root");
    }
    finally
    {
        if (Directory.Exists(extractionRoot))
        {
            Directory.Delete(extractionRoot, recursive: true);
        }
    }
}

static void VerifyPackagedDelta(
    string deltaArchivePath,
    string baseArchivePath,
    string expectedBaseVersion,
    string expectedVersion)
{
    string deltaArchive = Path.GetFullPath(deltaArchivePath);
    string baseArchive = Path.GetFullPath(baseArchivePath);
    if (!File.Exists(deltaArchive))
    {
        throw new FileNotFoundException("Packaged incremental ZIP was not found.", deltaArchive);
    }
    if (!File.Exists(baseArchive))
    {
        throw new FileNotFoundException("Packaged incremental base ZIP was not found.", baseArchive);
    }

    string root = Path.Combine(
        Path.GetTempPath(),
        "LoopstructorPackagedDeltaVerification-" + Guid.NewGuid().ToString("N"));
    string current = Path.Combine(root, "current", SecureZipExtractor.ReleaseArchiveRootDirectory);
    string extractedDelta = Path.Combine(root, "delta");
    string staging = Path.Combine(root, "staging");
    try
    {
        new SecureZipExtractor().ExtractReleasePackage(baseArchive, current);
        new ReleasePackageValidator().Validate(current, expectedBaseVersion, validateTargetSafety: true);
        new SecureZipExtractor().ExtractDeltaPackage(deltaArchive, extractedDelta);
        new DeltaPackageReconstructor().Reconstruct(
            extractedDelta,
            current,
            staging,
            expectedBaseVersion,
            expectedVersion);
        new ReleasePackageValidator().Validate(staging, expectedVersion);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void VerifyDefaultUpdateConfiguration(string root)
{
    string? originalOwner = Environment.GetEnvironmentVariable(UpdateConfigurationLoader.GitHubOwnerEnvironmentVariable);
    string? originalRepository = Environment.GetEnvironmentVariable(UpdateConfigurationLoader.GitHubRepositoryEnvironmentVariable);
    string? originalToken = Environment.GetEnvironmentVariable(UpdateConfigurationLoader.GitHubTokenEnvironmentVariable);
    string? originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    try
    {
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubOwnerEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubRepositoryEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubTokenEnvironmentVariable, null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        string applicationDirectory = Path.Combine(root, "default-update-configuration");
        Directory.CreateDirectory(applicationDirectory);
        UpdateCommandOptions options = UpdateCommandOptions.Parse(new[] { "check", "--current-version", "0.1.0" });
        LoadedUpdateConfiguration configuration = new UpdateConfigurationLoader().Load(options, applicationDirectory);

        Require(configuration.ConfigurationPath.Length == 0, "default update configuration does not require a file");
        Require(configuration.Source.GitHubOwner == "yingyu4451", "default GitHub owner");
        Require(configuration.Source.GitHubRepository == "Loopstructor-2-AutoPlayer", "default GitHub repository");
        Require(configuration.GitHubToken.Length == 0, "default update configuration does not invent a token");

        string retiredLayoutRoot = Path.Combine(root, "retired-config-layout");
        string retiredUpdaterDirectory = Path.Combine(retiredLayoutRoot, "updater");
        Directory.CreateDirectory(retiredUpdaterDirectory);
        File.WriteAllText(
            Path.Combine(retiredLayoutRoot, "autoplayer-update.json"),
            "{\"GitHubOwner\":\"retired-owner\",\"GitHubRepository\":\"retired-repository\"}");
        LoadedUpdateConfiguration retiredLayoutConfiguration = new UpdateConfigurationLoader().Load(
            options,
            retiredUpdaterDirectory);
        Require(
            retiredLayoutConfiguration.ConfigurationPath.Length == 0,
            "retired updater directory does not inherit parent configuration");
        Require(
            retiredLayoutConfiguration.Source.GitHubOwner == "yingyu4451"
            && retiredLayoutConfiguration.Source.GitHubRepository == "Loopstructor-2-AutoPlayer",
            "retired updater directory uses current built-in source defaults");

        string legacyConfigPath = Path.Combine(applicationDirectory, "autoplayer-update.json");
        File.WriteAllText(
            legacyConfigPath,
            "{\"GitHubOwner\":\"yingyu4451\",\"GitHubRepository\":\"gui2\"}");
        LoadedUpdateConfiguration migratedConfiguration = new UpdateConfigurationLoader().Load(
            options,
            applicationDirectory);
        Require(
            migratedConfiguration.Source.GitHubOwner == "yingyu4451"
            && migratedConfiguration.Source.GitHubRepository == "Loopstructor-2-AutoPlayer",
            "renamed published repository configuration migrates to current coordinates");
    }
    finally
    {
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubOwnerEnvironmentVariable, originalOwner);
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubRepositoryEnvironmentVariable, originalRepository);
        Environment.SetEnvironmentVariable(UpdateConfigurationLoader.GitHubTokenEnvironmentVariable, originalToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGitHubToken);
    }
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

static void VerifyReleasePackageExtraction(string root)
{
    SecureZipExtractor extractor = new();
    string archiveRoot = SecureZipExtractor.ReleaseArchiveRootDirectory;
    string validZip = Path.Combine(root, "wrapped-valid.zip");
    using (ZipArchive archive = ZipFile.Open(validZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry(archiveRoot + "/");
        ZipArchiveEntry entry = archive.CreateEntry(archiveRoot + "/folder/file.txt");
        using StreamWriter writer = new(entry.Open());
        writer.Write("wrapped");
    }

    string validDestination = Path.Combine(root, "wrapped-valid-extracted");
    extractor.ExtractReleasePackage(validZip, validDestination);
    Require(
        File.ReadAllText(Path.Combine(validDestination, "folder", "file.txt")) == "wrapped",
        "wrapped release extraction strips the fixed root directory");
    Require(
        !Directory.Exists(Path.Combine(validDestination, archiveRoot)),
        "wrapped release extraction does not retain a nested release directory");

    string implicitRootZip = Path.Combine(root, "wrapped-implicit-root.zip");
    using (ZipArchive archive = ZipFile.Open(implicitRootZip, ZipArchiveMode.Create))
    {
        ZipArchiveEntry entry = archive.CreateEntry(archiveRoot + "/file.txt");
        using StreamWriter writer = new(entry.Open());
        writer.Write("implicit");
    }
    extractor.ExtractReleasePackage(implicitRootZip, Path.Combine(root, "wrapped-implicit-root-extracted"));

    string flatZip = Path.Combine(root, "wrapped-flat.zip");
    using (ZipArchive archive = ZipFile.Open(flatZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry("file.txt");
    }
    ExpectInvalidData(
        () => extractor.ExtractReleasePackage(flatZip, Path.Combine(root, "wrapped-flat-extracted")),
        "flat release package rejection");

    string extraRootZip = Path.Combine(root, "wrapped-extra-root.zip");
    using (ZipArchive archive = ZipFile.Open(extraRootZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry(archiveRoot + "/file.txt");
        archive.CreateEntry("unexpected/file.txt");
    }
    ExpectInvalidData(
        () => extractor.ExtractReleasePackage(extraRootZip, Path.Combine(root, "wrapped-extra-root-extracted")),
        "second release archive root rejection");

    string wrongCaseZip = Path.Combine(root, "wrapped-wrong-case.zip");
    using (ZipArchive archive = ZipFile.Open(wrongCaseZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry(archiveRoot.ToLowerInvariant() + "/file.txt");
    }
    ExpectInvalidData(
        () => extractor.ExtractReleasePackage(wrongCaseZip, Path.Combine(root, "wrapped-wrong-case-extracted")),
        "release archive root case mismatch rejection");

    string rootFileZip = Path.Combine(root, "wrapped-root-file.zip");
    using (ZipArchive archive = ZipFile.Open(rootFileZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry(archiveRoot);
    }
    ExpectInvalidData(
        () => extractor.ExtractReleasePackage(rootFileZip, Path.Combine(root, "wrapped-root-file-extracted")),
        "release archive root file rejection");

    string duplicateZip = Path.Combine(root, "wrapped-duplicate.zip");
    using (ZipArchive archive = ZipFile.Open(duplicateZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry(archiveRoot + "/file.txt");
        archive.CreateEntry(archiveRoot + "/FILE.txt");
    }
    ExpectInvalidData(
        () => extractor.ExtractReleasePackage(duplicateZip, Path.Combine(root, "wrapped-duplicate-extracted")),
        "release archive case-colliding path rejection");
}

static void VerifyWrappedReleaseTransaction(string root)
{
    string fixtureRoot = Path.Combine(root, "wrapped-transaction");
    string target = Path.Combine(fixtureRoot, SecureZipExtractor.ReleaseArchiveRootDirectory);
    CreateRelease(target, "0.1.0");
    File.WriteAllText(Path.Combine(target, "autoplayer-update.json"), "{\"githubOwner\":\"configured\"}");

    string archiveSource = Path.Combine(fixtureRoot, "archive-source");
    string incomingRelease = Path.Combine(archiveSource, SecureZipExtractor.ReleaseArchiveRootDirectory);
    CreateRelease(incomingRelease, "0.2.0");
    string archivePath = Path.Combine(fixtureRoot, "release.zip");
    ZipFile.CreateFromDirectory(archiveSource, archivePath, CompressionLevel.Fastest, includeBaseDirectory: false);

    TransactionalInstaller installer = new(journalPath: Path.Combine(fixtureRoot, "wrapped-transaction.json"));
    string staging = installer.CreateStagingRoot(target);
    new SecureZipExtractor().ExtractReleasePackage(archivePath, staging);
    new ReleasePackageValidator().Validate(staging, "0.2.0");
    installer.Apply(staging, target, "0.2.0");

    Require(ReadReleaseVersion(target) == "0.2.0", "wrapped release replaces the current installation root");
    Require(File.Exists(Path.Combine(target, "autoplayer-update.json")), "wrapped release update preserves configuration");
    Require(
        !Directory.Exists(Path.Combine(target, SecureZipExtractor.ReleaseArchiveRootDirectory)),
        "wrapped release update does not create a nested installation root");
}

static void VerifyLegacyManagerEntryPointRejected(string root)
{
    string legacy = Path.Combine(root, "legacy-release");
    CreateRelease(legacy, "0.0.9", "manager/Loopstructor.AutoPlayer.Manager.exe");
    ExpectInvalidData(
        () => new ReleasePackageValidator().Validate(legacy, "0.0.9"),
        "legacy nested Manager entry point rejection");
    ExpectInvalidData(
        () => ManagerRestarter.CreateStartInfo(legacy),
        "legacy nested Manager restart rejection");
}

static void VerifyMissingEntryPointsRejected(string root)
{
    string release = Path.Combine(root, "missing-entry-points-release");
    CreateRelease(release, "0.1.0");
    string markerPath = Path.Combine(release, "autoplayer-release.json");
    using JsonDocument markerDocument = JsonDocument.Parse(File.ReadAllText(markerPath));
    Dictionary<string, object?> marker = markerDocument.RootElement
        .EnumerateObject()
        .Where(property => property.Name is not nameof(ReleaseMarker.ManagerPath) and not nameof(ReleaseMarker.UpdaterPath))
        .ToDictionary(
            property => property.Name,
            property => JsonSerializer.Deserialize<object?>(property.Value.GetRawText()),
            StringComparer.OrdinalIgnoreCase);
    File.WriteAllText(markerPath, JsonSerializer.Serialize(marker));
    WriteChecksums(release);

    ExpectInvalidData(
        () => new ReleasePackageValidator().Validate(release, "0.1.0"),
        "missing release entry points rejection");
}

static void VerifyRetiredUpdaterDirectoryRejected(string root)
{
    string release = Path.Combine(root, "retired-updater-directory-release");
    CreateRelease(release, "0.1.0");
    string retiredUpdaterDirectory = Path.Combine(release, "updater");
    Directory.CreateDirectory(retiredUpdaterDirectory);
    File.WriteAllText(
        Path.Combine(retiredUpdaterDirectory, "Loopstructor.AutoPlayer.Updater.exe"),
        "retired-layout");
    WriteChecksums(release);

    ExpectInvalidData(
        () => new ReleasePackageValidator().Validate(release, "0.1.0"),
        "retired updater directory rejection");
}

static void VerifyManagerRestartEntryPoint(string root)
{
    string release = Path.Combine(root, "restart-release");
    CreateRelease(release, "0.1.1");
    ProcessStartInfo startInfo = ManagerRestarter.CreateStartInfo(release);
    Require(
        startInfo.FileName == Path.Combine(release, "Loopstructor.AutoPlayer.Manager.exe"),
        "Updater restarts through the root Manager entry point");
    Require(startInfo.WorkingDirectory == release, "root Manager restart working directory");
    Require(
        startInfo.ArgumentList.SequenceEqual(new[] { "--restarted-after-update" }),
        "root Manager restart argument");
}

static void VerifyManagerEntryPointTraversalRejected(string root)
{
    string release = Path.Combine(root, "entry-traversal-release");
    CreateRelease(release, "0.1.1");
    ReleaseMarker marker = JsonSerializer.Deserialize<ReleaseMarker>(
        File.ReadAllText(Path.Combine(release, "autoplayer-release.json")))!;
    marker.ManagerPath = "../Loopstructor.AutoPlayer.Manager.exe";
    File.WriteAllText(Path.Combine(release, "autoplayer-release.json"), JsonSerializer.Serialize(marker));
    File.WriteAllText(Path.Combine(root, "Loopstructor.AutoPlayer.Manager.exe"), "outside");

    ExpectInvalidData(
        () => new ReleasePackageValidator().Validate(release, "0.1.1"),
        "Manager entry point traversal rejection");
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
    Require(string.IsNullOrEmpty(backup), "successful transaction does not retain a backup path");
    Require(
        Directory.GetDirectories(root, ".LoopstructorAutoPlayer-rollback-*", SearchOption.TopDirectoryOnly).Length == 0,
        "successful transaction removes the temporary rollback root");
    Require(
        Directory.GetDirectories(root, ".LoopstructorAutoPlayer-backup-*", SearchOption.TopDirectoryOnly).Length == 0,
        "successful transaction does not retain legacy backups");
    Require(File.Exists(Path.Combine(target, "autoplayer-update.json")), "update configuration preserved");
    Require(!File.Exists(installer.JournalPath), "completed transaction journal removed");
}

static void VerifyInterruptedRecovery(string root)
{
    string target = Path.Combine(root, "recovery-release");
    CreateRelease(target, "0.3.0");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-rollback-20260824-120000-12345678");
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
        Version = "0.5.2",
        Phase = "rollback-created",
        UpdatedAtUtc = DateTime.UtcNow
    };
    File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
    TransactionalInstaller installer = new(journalPath: journalPath);
    string message = installer.RecoverIncomplete(target);
    Require(message.Contains("已从中断的更新中恢复上一版本", StringComparison.Ordinal), "interrupted transaction reports restore");
    Require(ReadReleaseVersion(target) == "0.3.0", "interrupted transaction restored old target");
    Require(!File.Exists(journalPath), "recovery journal removed");
}

static void VerifyCommittedRecoveryCleanup(string root)
{
    string target = Path.Combine(root, "committed-release");
    CreateRelease(target, "0.4.0");
    string rollback = Path.Combine(root, ".LoopstructorAutoPlayer-rollback-20260824-120003-00112233");
    CreateRelease(rollback, "0.3.0");
    string staging = Path.Combine(root, ".LoopstructorAutoPlayer-staging-committed");
    Directory.CreateDirectory(staging);
    string journalPath = Path.Combine(root, "committed-transaction.json");
    File.WriteAllText(journalPath, JsonSerializer.Serialize(new UpdateTransactionJournal
    {
        TransactionId = "0011223344556677",
        TargetRoot = target,
        BackupRoot = rollback,
        StagingRoot = staging,
        Version = "0.4.0",
        Phase = "installed",
        UpdatedAtUtc = DateTime.UtcNow
    }));

    string message = new TransactionalInstaller(journalPath: journalPath).RecoverIncomplete(target);
    Require(message.Contains("清理临时回滚目录", StringComparison.Ordinal), "committed recovery reports rollback cleanup");
    Require(ReadReleaseVersion(target) == "0.4.0", "committed recovery keeps the installed release");
    Require(!Directory.Exists(rollback), "committed recovery removes the temporary rollback root");
    Require(!File.Exists(journalPath), "committed recovery removes its journal");
}

static void VerifyLegacyBackupCleanup(string root)
{
    string target = Path.Combine(root, "legacy-cleanup-release");
    CreateRelease(target, "0.4.0");
    string legacy = Path.Combine(root, ".LoopstructorAutoPlayer-backup-20260824-120004-44556677");
    CreateRelease(legacy, "0.3.0");
    TransactionalInstaller installer = new(journalPath: Path.Combine(root, "legacy-cleanup-transaction.json"));
    string staging = installer.CreateStagingRoot(target);
    CreateRelease(staging, "0.5.0");

    installer.Apply(staging, target, "0.5.0");

    Require(ReadReleaseVersion(target) == "0.5.0", "legacy cleanup transaction installs the new release");
    Require(!Directory.Exists(legacy), "strictly named legacy backup is removed after a valid update");
}

static void VerifyInvalidInstallRollback(string root)
{
    string target = Path.Combine(root, "invalid-release");
    CreateRelease(target, "0.5.2");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-rollback-20260824-120001-abcdef00");
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
    Require(ReadReleaseVersion(target) == "0.5.2", "invalid installed release rolled back");
    Require(!File.Exists(journalPath), "rollback journal removed");
    Require(Directory.GetDirectories(root, ".LoopstructorAutoPlayer-failed-*", SearchOption.TopDirectoryOnly).Length == 0,
        "invalid installed release is removed instead of retained outside the application root");
}

static void VerifyPreparedWindowRecovery(string root)
{
    string target = Path.Combine(root, "prepared-release");
    CreateRelease(target, "0.7.0");
    string backup = Path.Combine(root, ".LoopstructorAutoPlayer-rollback-20260824-120002-fedcba98");
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

static void CreateRelease(
    string root,
    string version,
    string managerPath = "Loopstructor.AutoPlayer.Manager.exe")
{
    Directory.CreateDirectory(Path.Combine(root, "manager"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "bepinex"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "plugin"));
    Directory.CreateDirectory(Path.Combine(root, "payload", "bepinex", "BepInEx", "core"));
    File.WriteAllText(Path.Combine(root, "Loopstructor.AutoPlayer.Manager.exe"), "root-launcher-" + version);
    File.WriteAllText(Path.Combine(root, "manager", "Loopstructor.AutoPlayer.Manager.exe"), version);
    File.WriteAllText(Path.Combine(root, "manager", "Loopstructor.AutoPlayer.Updater.exe"), version);
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
            ManagerPath = managerPath,
            UpdaterPath = "manager/Loopstructor.AutoPlayer.Updater.exe",
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
