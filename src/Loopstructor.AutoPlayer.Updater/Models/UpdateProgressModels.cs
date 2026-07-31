namespace Loopstructor.AutoPlayer.Updater.Models;

public enum UpdateProgressStage
{
    Preparing,
    Checking,
    Downloading,
    Verifying,
    Extracting,
    WaitingForProcesses,
    Installing,
    Restarting,
    Completed
}

public enum UpdateInstallPhase
{
    Prepared,
    BackupCreated,
    Installed,
    Validated
}

public sealed record PackageDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond);

public sealed record ArchiveExtractionProgress(
    long ExtractedBytes,
    long TotalBytes,
    int ExtractedFiles,
    int TotalFiles);

public sealed class UpdateProgressSnapshot
{
    public UpdateProgressStage Stage { get; init; }
    public int OverallPercent { get; init; }
    public string Message { get; init; } = string.Empty;
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public bool CanCancel { get; init; }
    public bool IsFailure { get; init; }
}

internal static class UpdateProgressMath
{
    public static int DownloadOverallPercent(long downloadedBytes, long totalBytes) =>
        Scale(downloadedBytes, totalBytes, 10, 60);

    public static int ExtractionOverallPercent(long extractedBytes, long totalBytes) =>
        Scale(extractedBytes, totalBytes, 68, 84);

    private static int Scale(long completed, long total, int minimum, int maximum)
    {
        if (total <= 0) return minimum;
        double ratio = Math.Clamp((double)completed / total, 0d, 1d);
        return Math.Clamp(minimum + (int)Math.Round((maximum - minimum) * ratio), minimum, maximum);
    }
}
