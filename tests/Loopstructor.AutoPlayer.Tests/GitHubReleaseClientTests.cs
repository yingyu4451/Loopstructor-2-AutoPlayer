using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;
using Loopstructor.AutoPlayer.Updater.Services;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class GitHubReleaseClientTests
{
    private const string Owner = "yingyu4451";
    private const string Repository = "gui2";

    [Fact]
    public async Task PublicReleaseResolution_AvoidsRestApiAndDownloadsExactTagPackage()
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("verified package bytes");
        UpdateManifest manifest = CreateManifest("0.1.4", packageBytes);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        RecordingHandler handler = new(request => PublicReleaseResponse(request, "v0.1.4", manifestBytes, packageBytes));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        ResolvedUpdate update = await client.ResolveLatestAsync();
        string destination = Path.Combine(Path.GetTempPath(), "autoplayer-public-download-" + Guid.NewGuid().ToString("N"), "package.zip");
        try
        {
            await client.DownloadVerifiedPackageAsync(update, destination);
            Assert.Equal(packageBytes, File.ReadAllBytes(destination));
        }
        finally
        {
            string? parent = Path.GetDirectoryName(destination);
            if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }

        Assert.Equal("v0.1.4", update.ReleaseTag);
        Assert.Equal(
            "https://github.com/yingyu4451/gui2/releases/download/v0.1.4/Loopstructor.AutoPlayer-0.1.4-win-x64.zip",
            update.PackageAsset.DownloadUri.ToString());
        Assert.DoesNotContain(handler.Requests, item => item.Uri.Host == "api.github.com");
        Assert.All(handler.Requests, item => Assert.Null(item.Authorization));
        Assert.Contains(
            handler.Requests,
            item => item.Uri.AbsolutePath == "/yingyu4451/gui2/releases/download/v0.1.4/Loopstructor.AutoPlayer-0.1.4-win-x64.zip");
        Assert.DoesNotContain(
            handler.Requests,
            item => item.Uri.AbsolutePath.Contains("/releases/latest/download/Loopstructor.AutoPlayer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicReleaseResolution_RejectsTagManifestVersionMismatch()
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("package");
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(CreateManifest("0.1.5", packageBytes));
        RecordingHandler handler = new(request => PublicManifestResponse(request, "v0.1.4", manifestBytes));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ResolveLatestAsync());

        Assert.Contains("does not match manifest version", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://github.com/yingyu4451/gui2/releases/tag/v0.1.4")]
    [InlineData("https://evil.example/yingyu4451/gui2/releases/tag/v0.1.4")]
    [InlineData("https://github.com/attacker/gui2/releases/tag/v0.1.4")]
    [InlineData("https://github.com:444/yingyu4451/gui2/releases/tag/v0.1.4")]
    [InlineData("https://user@github.com/yingyu4451/gui2/releases/tag/v0.1.4")]
    [InlineData("https://github.com/login?return_to=%2Fyingyu4451%2Fgui2")]
    public async Task PublicReleaseResolution_RejectsUnsafeLatestRedirect(string location)
    {
        RecordingHandler handler = new(_ => Redirect(location));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ResolveLatestAsync());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PublicReleaseResolution_RejectsUntrustedAssetRedirectWithoutContactingIt()
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/yingyu4451/gui2/releases/latest" => Redirect(
                "https://github.com/yingyu4451/gui2/releases/tag/v0.1.4"),
            "/yingyu4451/gui2/releases/download/v0.1.4/autoplayer-update-manifest.json" => Redirect(
                "https://evil.example/manifest"),
            _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
        });
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ResolveLatestAsync());

        Assert.DoesNotContain(handler.Requests, item => item.Uri.Host == "evil.example");
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("v0.1.4")]
    [InlineData("01.1.4")]
    [InlineData("0.1.4+bad..metadata")]
    public async Task PublicReleaseResolution_RejectsNonCanonicalManifestVersion(string version)
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("package");
        UpdateManifest manifest = CreateManifest("0.1.4", packageBytes);
        manifest.Version = version;
        manifest.AssetName = $"Loopstructor.AutoPlayer-{version}-win-x64.zip";
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        RecordingHandler handler = new(request => PublicManifestResponse(request, "v0.1.4", manifestBytes));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ResolveLatestAsync());
    }

    [Fact]
    public async Task PublicReleaseResolution_RejectsPackageNameThatDoesNotMatchVersion()
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("package");
        UpdateManifest manifest = CreateManifest("0.1.4", packageBytes);
        manifest.AssetName = "package.zip";
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        RecordingHandler handler = new(request => PublicManifestResponse(request, "v0.1.4", manifestBytes));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ResolveLatestAsync());
    }

    [Fact]
    public async Task PackageHashMismatch_DeletesPartialDownload()
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("package with a bad declared hash");
        UpdateManifest manifest = CreateManifest("0.1.4", packageBytes);
        manifest.Sha256 = new string('0', 64);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        RecordingHandler handler = new(request => PublicReleaseResponse(request, "v0.1.4", manifestBytes, packageBytes));
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings());
        ResolvedUpdate update = await client.ResolveLatestAsync();
        string destination = Path.Combine(Path.GetTempPath(), "autoplayer-hash-mismatch-" + Guid.NewGuid().ToString("N"), "package.zip");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedPackageAsync(update, destination));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            string? parent = Path.GetDirectoryName(destination);
            if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task TokenReleaseResolution_UsesApiAssetsAndNeverForwardsBearerToCdn()
    {
        const string token = "test-token-not-a-secret";
        byte[] packageBytes = Encoding.UTF8.GetBytes("private package bytes");
        UpdateManifest manifest = CreateManifest("0.1.4", packageBytes);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        string releaseJson = $$"""
        {
          "tag_name": "v0.1.4",
          "html_url": "https://github.com/{{Owner}}/{{Repository}}/releases/tag/v0.1.4",
          "assets": [
            {
              "name": "autoplayer-update-manifest.json",
              "url": "https://api.github.com/repos/{{Owner}}/{{Repository}}/releases/assets/1001",
              "browser_download_url": "https://evil.example/ignored-manifest",
              "size": {{manifestBytes.Length}}
            },
            {
              "name": "{{manifest.AssetName}}",
              "url": "https://api.github.com/repos/{{Owner}}/{{Repository}}/releases/assets/1002",
              "browser_download_url": "https://evil.example/ignored-package",
              "size": {{packageBytes.Length}}
            }
          ]
        }
        """;
        RecordingHandler handler = new(request => request.RequestUri!.ToString() switch
        {
            "https://api.github.com/repos/yingyu4451/gui2/releases/latest" => JsonResponse(releaseJson),
            "https://api.github.com/repos/yingyu4451/gui2/releases/assets/1001" => Redirect(
                "https://release-assets.githubusercontent.com/private-manifest?signature=manifest"),
            "https://release-assets.githubusercontent.com/private-manifest?signature=manifest" => BytesResponse(manifestBytes),
            "https://api.github.com/repos/yingyu4451/gui2/releases/assets/1002" => Redirect(
                "https://release-assets.githubusercontent.com/private-package?signature=package"),
            "https://release-assets.githubusercontent.com/private-package?signature=package" => BytesResponse(packageBytes),
            _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
        });
        using HttpClient httpClient = new(handler);
        GitHubReleaseClient client = new(httpClient, CreateSettings(), token);

        ResolvedUpdate update = await client.ResolveLatestAsync();
        string destination = Path.Combine(Path.GetTempPath(), "autoplayer-private-download-" + Guid.NewGuid().ToString("N"), "package.zip");
        try
        {
            await client.DownloadVerifiedPackageAsync(update, destination);
            Assert.Equal(packageBytes, File.ReadAllBytes(destination));
        }
        finally
        {
            string? parent = Path.GetDirectoryName(destination);
            if (parent != null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }

        Assert.All(
            handler.Requests.Where(item => item.Uri.Host == "api.github.com"),
            item => Assert.Equal("Bearer " + token, item.Authorization));
        Assert.All(
            handler.Requests.Where(item => item.Uri.Host == "release-assets.githubusercontent.com"),
            item => Assert.Null(item.Authorization));
        Assert.DoesNotContain(handler.Requests, item => item.Uri.Host == "evil.example");
    }

    private static HttpResponseMessage PublicReleaseResponse(
        HttpRequestMessage request,
        string releaseTag,
        byte[] manifestBytes,
        byte[] packageBytes)
    {
        string packageName = $"Loopstructor.AutoPlayer-{releaseTag[1..]}-win-x64.zip";
        return request.RequestUri!.ToString() switch
        {
            "https://github.com/yingyu4451/gui2/releases/latest" => Redirect(
                $"https://github.com/yingyu4451/gui2/releases/tag/{releaseTag}"),
            var value when value == $"https://github.com/yingyu4451/gui2/releases/download/{releaseTag}/autoplayer-update-manifest.json" => Redirect(
                "https://release-assets.githubusercontent.com/public-manifest?signature=manifest"),
            "https://release-assets.githubusercontent.com/public-manifest?signature=manifest" => BytesResponse(manifestBytes),
            var value when value == $"https://github.com/yingyu4451/gui2/releases/download/{releaseTag}/{packageName}" => Redirect(
                "https://release-assets.githubusercontent.com/public-package?signature=package"),
            "https://release-assets.githubusercontent.com/public-package?signature=package" => BytesResponse(packageBytes),
            _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
        };
    }

    private static HttpResponseMessage PublicManifestResponse(
        HttpRequestMessage request,
        string releaseTag,
        byte[] manifestBytes) => request.RequestUri!.ToString() switch
    {
        "https://github.com/yingyu4451/gui2/releases/latest" => Redirect(
            $"https://github.com/yingyu4451/gui2/releases/tag/{releaseTag}"),
        var value when value == $"https://github.com/yingyu4451/gui2/releases/download/{releaseTag}/autoplayer-update-manifest.json" => Redirect(
            "https://release-assets.githubusercontent.com/public-manifest?signature=manifest"),
        "https://release-assets.githubusercontent.com/public-manifest?signature=manifest" => BytesResponse(manifestBytes),
        _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
    };

    private static UpdateManifest CreateManifest(string version, byte[] packageBytes) => new()
    {
        SchemaVersion = 2,
        Version = version,
        RuntimeIdentifier = "win-x64",
        AssetName = $"Loopstructor.AutoPlayer-{version}-win-x64.zip",
        Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
        Size = packageBytes.Length
    };

    private static UpdateSourceSettings CreateSettings() => new()
    {
        GitHubOwner = Owner,
        GitHubRepository = Repository,
        RuntimeIdentifier = "win-x64",
        ManifestAssetName = "autoplayer-update-manifest.json"
    };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) }
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthenticationHeaderValue? authorization = request.Headers.Authorization;
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                authorization == null ? null : authorization.Scheme + " " + authorization.Parameter));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed record RequestSnapshot(Uri Uri, string? Authorization);
}
