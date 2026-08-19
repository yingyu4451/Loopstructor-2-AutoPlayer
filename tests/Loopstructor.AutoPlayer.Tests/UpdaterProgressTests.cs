using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class UpdaterProgressTests
{
    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(50, 100, 35)]
    [InlineData(100, 100, 60)]
    [InlineData(200, 100, 60)]
    [InlineData(1, 0, 10)]
    public void DownloadOverallPercent_UsesBoundedStageRange(long completed, long total, int expected)
    {
        Assert.Equal(expected, UpdateProgressMath.DownloadOverallPercent(completed, total));
    }

    [Theory]
    [InlineData(0, 100, 68)]
    [InlineData(50, 100, 76)]
    [InlineData(100, 100, 84)]
    public void ExtractionOverallPercent_UsesBoundedStageRange(long completed, long total, int expected)
    {
        Assert.Equal(expected, UpdateProgressMath.ExtractionOverallPercent(completed, total));
    }

    [Fact]
    public async Task DownloadVerifiedPackage_ReportsTrustedByteProgress()
    {
        byte[] package = Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 251)).ToArray();
        UpdateManifest manifest = Manifest("0.1.8", package);
        ResolvedUpdate update = Resolved(manifest);
        using HttpClient httpClient = new(new StaticContentHandler(package));
        GitHubReleaseClient client = new(httpClient, Settings());
        List<PackageDownloadProgress> reports = new();
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-progress-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, manifest.AssetName);
        try
        {
            await client.DownloadVerifiedPackageAsync(
                update,
                destination,
                new InlineProgress<PackageDownloadProgress>(reports.Add));

            Assert.NotEmpty(reports);
            Assert.Equal(0, reports[0].DownloadedBytes);
            PackageDownloadProgress final = reports[^1];
            Assert.Equal(package.LongLength, final.DownloadedBytes);
            Assert.Equal(package.LongLength, final.TotalBytes);
            Assert.True(final.BytesPerSecond > 0);
            Assert.Equal(package, File.ReadAllBytes(destination));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedPackage_IgnoresProgressConsumerFailure()
    {
        byte[] package = new byte[256 * 1024];
        UpdateManifest manifest = Manifest("0.1.8", package);
        using HttpClient httpClient = new(new StaticContentHandler(package));
        GitHubReleaseClient client = new(httpClient, Settings());
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-progress-failure-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, manifest.AssetName);
        try
        {
            await client.DownloadVerifiedPackageAsync(
                Resolved(manifest),
                destination,
                new InlineProgress<PackageDownloadProgress>(_ => throw new InvalidOperationException("display failed")));

            Assert.True(File.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractReleasePackage_ReportsExpandedByteProgress()
    {
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-extract-progress-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string archive = Path.Combine(root, "package.zip");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(source, SecureZipExtractor.ReleaseArchiveRootDirectory));
        byte[] payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(
            Path.Combine(source, SecureZipExtractor.ReleaseArchiveRootDirectory, "payload.bin"),
            payload);
        ZipFile.CreateFromDirectory(source, archive);
        List<ArchiveExtractionProgress> reports = new();
        try
        {
            new SecureZipExtractor().ExtractReleasePackage(
                archive,
                destination,
                new InlineProgress<ArchiveExtractionProgress>(reports.Add));

            Assert.NotEmpty(reports);
            ArchiveExtractionProgress final = reports[^1];
            Assert.Equal(payload.LongLength, final.ExtractedBytes);
            Assert.Equal(payload.LongLength, final.TotalBytes);
            Assert.Equal(1, final.ExtractedFiles);
            Assert.Equal(1, final.TotalFiles);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractReleasePackage_HonorsCancellationBeforeWriting()
    {
        string root = Path.Combine(Path.GetTempPath(), "autoplayer-extract-cancel-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string archive = Path.Combine(root, "package.zip");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(source, SecureZipExtractor.ReleaseArchiveRootDirectory));
        File.WriteAllText(
            Path.Combine(source, SecureZipExtractor.ReleaseArchiveRootDirectory, "payload.txt"),
            "payload");
        ZipFile.CreateFromDirectory(source, archive);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                new SecureZipExtractor().ExtractReleasePackage(
                    archive,
                    destination,
                    progress: null,
                    cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_DemoUi_PreservesApplyMode()
    {
        UpdateCommandOptions options = UpdateCommandOptions.Parse(new[]
        {
            "apply",
            "--target",
            Path.GetTempPath(),
            "--current-version",
            "0.1.8",
            "--staged-run",
            "--demo-ui"
        });

        Assert.Equal(UpdateCommand.Apply, options.Command);
        Assert.True(options.StagedRun);
        Assert.True(options.DemoUi);
        Assert.False(options.JsonOutput);
    }

    [Fact]
    public void Parse_DemoUiWithJson_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => UpdateCommandOptions.Parse(new[]
        {
            "apply",
            "--target",
            Path.GetTempPath(),
            "--current-version",
            "0.1.8",
            "--staged-run",
            "--demo-ui",
            "--json"
        }));
    }

    [Fact]
    public void OperationCanceled_WithoutUserCancellation_IsReportedAsNetworkTimeout()
    {
        string message = Loopstructor.AutoPlayer.Updater.Program.GetUserFacingFailureMessage(
            new TaskCanceledException(),
            cancellationRequested: false);

        Assert.Contains("GitHub", message, StringComparison.Ordinal);
        Assert.Contains("超时", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationCanceled_WithUserCancellation_IsReportedAsCancellation()
    {
        string message = Loopstructor.AutoPlayer.Updater.Program.GetUserFacingFailureMessage(
            new OperationCanceledException(),
            cancellationRequested: true);

        Assert.Equal("更新已取消。", message);
    }

    [Fact]
    public void UpdateCommitGate_CancellationWinsBeforeCommit()
    {
        using UpdateCommitGate gate = new();

        Assert.True(gate.TryCancel());
        Assert.True(gate.Token.IsCancellationRequested);
        Assert.False(gate.TryBeginCommit());
    }

    [Fact]
    public void UpdateCommitGate_CommitPreventsLateCancellation()
    {
        using UpdateCommitGate gate = new();

        Assert.True(gate.TryBeginCommit());
        Assert.False(gate.TryCancel());
        Assert.False(gate.Token.IsCancellationRequested);
    }

    private static UpdateManifest Manifest(string version, byte[] package) => new()
    {
        SchemaVersion = 2,
        Version = version,
        RuntimeIdentifier = "win-x64",
        AssetName = $"Loopstructor.AutoPlayer-{version}-win-x64.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
        Size = package.LongLength
    };

    private static ResolvedUpdate Resolved(UpdateManifest manifest) => new()
    {
        Manifest = manifest,
        PackageAsset = new GitHubReleaseAsset
        {
            Name = manifest.AssetName,
            DownloadUri = new Uri(
                $"https://github.com/yingyu4451/Loopstructor-2-AutoPlayer/releases/download/v{manifest.Version}/{manifest.AssetName}"),
            Size = manifest.Size
        },
        ReleaseTag = "v" + manifest.Version,
        ReleasePageUrl = "https://github.com/yingyu4451/Loopstructor-2-AutoPlayer/releases/tag/v" + manifest.Version
    };

    private static UpdateSourceSettings Settings() => new()
    {
        GitHubOwner = "yingyu4451",
        GitHubRepository = "Loopstructor-2-AutoPlayer",
        RuntimeIdentifier = "win-x64",
        ManifestAssetName = "autoplayer-update-manifest.json"
    };

    private sealed class StaticContentHandler : HttpMessageHandler
    {
        private readonly byte[] _content;

        public StaticContentHandler(byte[] content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content)
            });
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public InlineProgress(Action<T> callback)
        {
            _callback = callback;
        }

        public void Report(T value) => _callback(value);
    }
}
