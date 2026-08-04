using System.Diagnostics;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

internal sealed class DeltaPackageReconstructor
{
    private readonly ReleasePackageValidator _validator;

    public DeltaPackageReconstructor(ReleasePackageValidator? validator = null)
    {
        _validator = validator ?? new ReleasePackageValidator();
    }

    public void Reconstruct(
        string extractedDeltaRoot,
        string currentRoot,
        string stagingRoot,
        string expectedFromVersion,
        string expectedTargetVersion,
        IProgress<ArchiveExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string deltaRoot = ReleasePackageValidator.NormalizeRoot(extractedDeltaRoot);
        string current = ReleasePackageValidator.NormalizeRoot(currentRoot);
        string staging = ReleasePackageValidator.NormalizeRoot(stagingRoot);
        if (!Directory.Exists(deltaRoot))
        {
            throw new DirectoryNotFoundException("增量更新包解压目录不存在：" + deltaRoot);
        }

        if (Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any())
        {
            throw new IOException("增量更新暂存目录必须为空：" + staging);
        }

        ReleaseMarker currentMarker = _validator.Validate(
            current,
            expectedFromVersion,
            validateTargetSafety: true);
        if (!string.Equals(currentMarker.Version, expectedFromVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"当前安装版本 {currentMarker.Version} 与增量包起始版本 {expectedFromVersion} 不完全一致。");
        }

        string deltaChecksumPath = Path.Combine(deltaRoot, "checksums.sha256");
        string payloadRoot = Path.Combine(deltaRoot, "files");
        if (!Directory.Exists(payloadRoot))
        {
            throw new InvalidDataException("增量更新包缺少 files 目录。");
        }

        string[] topLevelDirectories = Directory.GetDirectories(deltaRoot, "*", SearchOption.TopDirectoryOnly);
        string[] topLevelFiles = Directory.GetFiles(deltaRoot, "*", SearchOption.TopDirectoryOnly);
        if (topLevelDirectories.Length != 1
            || !string.Equals(topLevelDirectories[0], payloadRoot, StringComparison.Ordinal)
            || topLevelFiles.Length != 1
            || !string.Equals(topLevelFiles[0], deltaChecksumPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("增量更新包根目录只能包含 checksums.sha256 和 files 目录。");
        }

        IReadOnlyDictionary<string, ReleaseChecksumEntry> targetCatalog =
            ReleasePackageValidator.ReadChecksumCatalog(deltaChecksumPath);
        IReadOnlyDictionary<string, ReleaseChecksumEntry> currentCatalog =
            ReleasePackageValidator.ReadChecksumCatalog(Path.Combine(current, "checksums.sha256"));

        Dictionary<string, ReleaseChecksumEntry> changed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReleaseChecksumEntry targetEntry in targetCatalog.Values)
        {
            if (!currentCatalog.TryGetValue(targetEntry.RelativePath, out ReleaseChecksumEntry? currentEntry)
                || !string.Equals(currentEntry.Sha256, targetEntry.Sha256, StringComparison.Ordinal))
            {
                changed.Add(targetEntry.RelativePath, targetEntry);
            }
        }

        IReadOnlyList<string> deltaFiles = ReleasePackageValidator.EnumerateRegularFiles(deltaRoot);
        string payloadPrefix = payloadRoot + Path.DirectorySeparatorChar;
        Dictionary<string, string> payloadFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in deltaFiles)
        {
            if (string.Equals(file, deltaChecksumPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!file.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "增量更新包包含不允许的文件：" + Path.GetRelativePath(deltaRoot, file).Replace('\\', '/'));
            }

            string relative = Path.GetRelativePath(payloadRoot, file);
            if (!changed.TryGetValue(relative, out ReleaseChecksumEntry? expected)
                || !string.Equals(relative, expected.RelativePath, StringComparison.Ordinal)
                || !payloadFiles.TryAdd(relative, file))
            {
                throw new InvalidDataException(
                    "增量更新包包含多余、重复或大小写不匹配的载荷：" + relative.Replace('\\', '/'));
            }
        }

        foreach (ReleaseChecksumEntry expected in changed.Values)
        {
            if (!payloadFiles.ContainsKey(expected.RelativePath))
            {
                throw new InvalidDataException(
                    "增量更新包缺少变更文件：" + expected.RelativePath.Replace('\\', '/'));
            }
        }

        Directory.CreateDirectory(staging);
        List<CopyPlan> copies = new(targetCatalog.Count);
        long totalBytes = 0;
        foreach (ReleaseChecksumEntry targetEntry in targetCatalog.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            string source;
            if (changed.ContainsKey(targetEntry.RelativePath))
            {
                source = payloadFiles[targetEntry.RelativePath];
            }
            else
            {
                ReleaseChecksumEntry currentEntry = currentCatalog[targetEntry.RelativePath];
                source = ResolveContainedFile(current, currentEntry.RelativePath, "当前安装文件");
            }

            long length = new FileInfo(source).Length;
            totalBytes = checked(totalBytes + length);
            if (totalBytes > SecureZipExtractor.MaximumExpandedBytes)
            {
                throw new InvalidDataException("增量更新重建后的文件总大小超过安全限制。");
            }

            copies.Add(new CopyPlan(source, targetEntry));
        }

        long copiedBytes = 0;
        int copiedFiles = 0;
        TimeSpan lastReportedAt = TimeSpan.Zero;
        Stopwatch clock = Stopwatch.StartNew();
        ReportProgressSafely(progress, new ArchiveExtractionProgress(0, totalBytes, 0, copies.Count));
        foreach (CopyPlan copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ResolveContainedDestination(staging, copy.Entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyAndVerify(
                copy.Source,
                destination,
                copy.Entry.Sha256,
                cancellationToken,
                bytes =>
                {
                    copiedBytes += bytes;
                    TimeSpan elapsed = clock.Elapsed;
                    if (elapsed - lastReportedAt >= TimeSpan.FromMilliseconds(100))
                    {
                        ReportProgressSafely(
                            progress,
                            new ArchiveExtractionProgress(copiedBytes, totalBytes, copiedFiles, copies.Count));
                        lastReportedAt = elapsed;
                    }
                });
            copiedFiles++;
            ReportProgressSafely(
                progress,
                new ArchiveExtractionProgress(copiedBytes, totalBytes, copiedFiles, copies.Count));
        }

        File.Copy(deltaChecksumPath, Path.Combine(staging, "checksums.sha256"), overwrite: false);
        ReleaseMarker targetMarker = _validator.Validate(staging, expectedTargetVersion);
        if (!string.Equals(targetMarker.Version, expectedTargetVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"增量更新目标版本 {targetMarker.Version} 与清单版本 {expectedTargetVersion} 不完全一致。");
        }
    }

    private static string ResolveContainedFile(string root, string relative, string label)
    {
        string full = ResolveContainedDestination(root, relative);
        if (!File.Exists(full) || File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(label + "不存在或不是普通文件：" + relative.Replace('\\', '/'));
        }

        return full;
    }

    private static string ResolveContainedDestination(string root, string relative)
    {
        string prefix = root + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("增量更新路径超出暂存目录：" + relative.Replace('\\', '/'));
        }

        return full;
    }

    private static void CopyAndVerify(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken,
        Action<int> reportBytes)
    {
        using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            reportBytes(read);
        }

        byte[] actual = hash.GetHashAndReset();
        byte[] expected = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                "增量更新重建文件校验失败：" + Path.GetFileName(destination));
        }
    }

    private static void ReportProgressSafely(
        IProgress<ArchiveExtractionProgress>? progress,
        ArchiveExtractionProgress value)
    {
        try { progress?.Report(value); } catch { }
    }

    private sealed record CopyPlan(string Source, ReleaseChecksumEntry Entry);
}
