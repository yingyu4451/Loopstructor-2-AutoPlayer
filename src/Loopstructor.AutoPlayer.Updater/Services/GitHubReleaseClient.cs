using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class GitHubReleaseClient
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex CanonicalSemanticVersionPattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly UpdateSourceSettings _settings;
    private readonly string _token;

    public GitHubReleaseClient(HttpClient httpClient, UpdateSourceSettings settings, string? token = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _token = token ?? string.Empty;
    }

    public Task<ResolvedUpdate> ResolveLatestAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(_token)
            ? ResolveLatestPublicAsync(cancellationToken)
            : ResolveLatestApiAsync(cancellationToken);

    public async Task DownloadVerifiedPackageAsync(
        ResolvedUpdate update,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateResolvedPackage(update);
        if (File.Exists(destinationPath))
        {
            throw new IOException("Package destination already exists: " + destinationPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using HttpResponseMessage response = await SendDownloadWithRedirectsAsync(update.PackageAsset.DownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.Manifest.Size)
        {
            throw new InvalidDataException($"Downloaded content length {contentLength} does not match manifest size {update.Manifest.Size}.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > update.Manifest.Size || total > MaximumPackageBytes)
                {
                    throw new InvalidDataException("Downloaded package exceeded the declared size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
            if (total != update.Manifest.Size)
            {
                throw new InvalidDataException($"Downloaded {total} bytes; expected {update.Manifest.Size}.");
            }

            byte[] actual = hash.GetHashAndReset();
            byte[] expected = Convert.FromHexString(update.Manifest.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException("Downloaded package SHA-256 does not match the release manifest value.");
            }
        }
        catch
        {
            await output.DisposeAsync();
            try { File.Delete(destinationPath); } catch { }
            throw;
        }
    }

    private async Task<ResolvedUpdate> ResolveLatestPublicAsync(CancellationToken cancellationToken)
    {
        Uri latestReleaseUri = BuildWebUri("releases", "latest");
        using HttpRequestMessage request = CreateRequest(latestReleaseUri, "text/html", includeToken: false);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub latest release was not found for the configured repository.");
        }

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("GitHub latest release did not redirect to a tagged release.");
        }

        Uri releasePageUri = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(latestReleaseUri, response.Headers.Location);
        string releaseTag = ValidateReleasePageUri(releasePageUri);
        Uri manifestUri = BuildReleaseAssetUri(releaseTag, _settings.ManifestAssetName);
        ValidateReleaseAssetUri(manifestUri, releaseTag, _settings.ManifestAssetName);
        UpdateManifest manifest = await DownloadAndValidateManifestAsync(manifestUri, cancellationToken);
        ValidateReleaseTag(releaseTag, manifest.Version);

        Uri packageUri = BuildReleaseAssetUri(releaseTag, manifest.AssetName);
        ValidateReleaseAssetUri(packageUri, releaseTag, manifest.AssetName);
        return new ResolvedUpdate
        {
            Manifest = manifest,
            PackageAsset = new GitHubReleaseAsset
            {
                Name = manifest.AssetName,
                DownloadUri = packageUri,
                Size = manifest.Size
            },
            ReleaseTag = releaseTag,
            ReleasePageUrl = releasePageUri.ToString()
        };
    }

    private async Task<ResolvedUpdate> ResolveLatestApiAsync(CancellationToken cancellationToken)
    {
        Uri releaseUri = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(_settings.GitHubOwner)}/{Uri.EscapeDataString(_settings.GitHubRepository)}/releases/latest");
        using HttpRequestMessage request = CreateRequest(releaseUri, "application/vnd.github+json", includeToken: true);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub latest release was not found for the configured repository.");
        }

        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseResponse? release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            responseStream,
            JsonOptions,
            cancellationToken);
        if (release == null)
        {
            throw new InvalidDataException("GitHub returned an empty release response.");
        }

        GitHubAssetResponse manifestAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, _settings.ManifestAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Release is missing " + _settings.ManifestAssetName + ".");
        if (manifestAsset.Size is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("GitHub manifest asset size is outside the accepted range.");
        }

        Uri manifestUri = ValidateApiAssetUri(manifestAsset.ApiUrl);
        byte[] manifestBytes = await DownloadSmallAssetAsync(manifestUri, cancellationToken);
        if (manifestBytes.LongLength != manifestAsset.Size)
        {
            throw new InvalidDataException(
                $"GitHub manifest asset size {manifestAsset.Size} does not match downloaded size {manifestBytes.LongLength}.");
        }

        UpdateManifest manifest = DeserializeAndValidateManifest(manifestBytes);
        ValidateReleaseTag(release.TagName, manifest.Version);
        GitHubAssetResponse package = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, manifest.AssetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Release does not contain manifest package asset " + manifest.AssetName + ".");
        if (package.Size != manifest.Size)
        {
            throw new InvalidDataException($"GitHub asset size {package.Size} does not match manifest size {manifest.Size}.");
        }

        return new ResolvedUpdate
        {
            Manifest = manifest,
            PackageAsset = new GitHubReleaseAsset
            {
                Name = package.Name,
                DownloadUri = ValidateApiAssetUri(package.ApiUrl),
                Size = package.Size
            },
            ReleaseTag = release.TagName,
            ReleasePageUrl = BuildReleasePageUri(release.TagName).ToString()
        };
    }

    private async Task<UpdateManifest> DownloadAndValidateManifestAsync(
        Uri manifestUri,
        CancellationToken cancellationToken)
    {
        byte[] manifestBytes = await DownloadSmallAssetAsync(manifestUri, cancellationToken);
        return DeserializeAndValidateManifest(manifestBytes);
    }

    private UpdateManifest DeserializeAndValidateManifest(byte[] manifestBytes)
    {
        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions);
        if (manifest == null)
        {
            throw new InvalidDataException("Update manifest is empty.");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    private async Task<byte[]> DownloadSmallAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendDownloadWithRedirectsAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
        {
            throw new InvalidDataException("Update manifest is unexpectedly large.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumManifestBytes)
            {
                throw new InvalidDataException("Update manifest exceeded the size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendDownloadWithRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri current = ValidateDownloadUri(initialUri.ToString());
        for (int redirect = 0; redirect <= 6; redirect++)
        {
            bool includeToken = IsGitHubApiHost(current.Host);
            using HttpRequestMessage request = CreateRequest(current, "application/octet-stream", includeToken);
            HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                Uri next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                response.Dispose();
                current = ValidateAssetRedirect(next);
                continue;
            }

            return response;
        }

        throw new HttpRequestException("GitHub download exceeded the redirect limit.");
    }

    private HttpRequestMessage CreateRequest(Uri uri, string accept, bool includeToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Loopstructor-AutoPlayer-Updater/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (IsGitHubApiHost(uri.Host))
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        if (includeToken && !string.IsNullOrWhiteSpace(_token))
        {
            if (!IsGitHubApiHost(uri.Host))
            {
                throw new InvalidOperationException("GitHub credentials may only be sent to api.github.com.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    private void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidDataException("Unsupported update manifest schema: " + manifest.SchemaVersion);
        }

        if (string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Version.Length > 128
            || !CanonicalSemanticVersionPattern.IsMatch(manifest.Version)
            || !SemanticVersion.TryParse(manifest.Version, out _))
        {
            throw new InvalidDataException("Manifest version is not canonical SemVer.");
        }

        if (!string.Equals(manifest.RuntimeIdentifier, "win-x64", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest runtime identifier does not match this updater.");
        }

        string expectedAssetName = $"Loopstructor.AutoPlayer-{manifest.Version}-win-x64.zip";
        if (string.IsNullOrWhiteSpace(manifest.AssetName)
            || !string.Equals(Path.GetFileName(manifest.AssetName), manifest.AssetName, StringComparison.Ordinal)
            || !string.Equals(manifest.AssetName, expectedAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest package asset name does not match its version and runtime.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Manifest SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (manifest.Size <= 0 || manifest.Size > MaximumPackageBytes)
        {
            throw new InvalidDataException("Manifest package size is outside the accepted range.");
        }

        manifest.Sha256 = manifest.Sha256.ToLowerInvariant();
    }

    private void ValidateResolvedPackage(ResolvedUpdate update)
    {
        ValidateManifest(update.Manifest);
        ValidateReleaseTag(update.ReleaseTag, update.Manifest.Version);
        if (!string.Equals(update.PackageAsset.Name, update.Manifest.AssetName, StringComparison.Ordinal)
            || update.PackageAsset.Size != update.Manifest.Size)
        {
            throw new InvalidDataException("Resolved package metadata does not match the release manifest.");
        }

        if (IsGitHubWebHost(update.PackageAsset.DownloadUri.Host))
        {
            ValidateReleaseAssetUri(update.PackageAsset.DownloadUri, update.ReleaseTag, update.Manifest.AssetName);
        }
        else if (IsGitHubApiHost(update.PackageAsset.DownloadUri.Host))
        {
            ValidateApiAssetUri(update.PackageAsset.DownloadUri.ToString());
        }
        else
        {
            throw new InvalidDataException("Resolved package URL must start at GitHub, not a release CDN.");
        }
    }

    private string ValidateReleasePageUri(Uri uri)
    {
        ValidateBaseUri(uri);
        if (!IsGitHubWebHost(uri.Host) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidDataException("Latest release redirected outside the configured GitHub repository.");
        }

        string[] segments = GetDecodedPathSegments(uri);
        if (segments.Length != 5
            || !string.Equals(segments[0], _settings.GitHubOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], _settings.GitHubRepository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "releases", StringComparison.Ordinal)
            || !string.Equals(segments[3], "tag", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(segments[4]))
        {
            throw new InvalidDataException("Latest release redirected outside the configured GitHub repository.");
        }

        return segments[4];
    }

    private void ValidateReleaseAssetUri(Uri uri, string releaseTag, string assetName)
    {
        ValidateBaseUri(uri);
        string[] segments = GetDecodedPathSegments(uri);
        if (!IsGitHubWebHost(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || segments.Length != 6
            || !string.Equals(segments[0], _settings.GitHubOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], _settings.GitHubRepository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "releases", StringComparison.Ordinal)
            || !string.Equals(segments[3], "download", StringComparison.Ordinal)
            || !string.Equals(segments[4], releaseTag, StringComparison.Ordinal)
            || !string.Equals(segments[5], assetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Release asset URL is outside the configured release and repository.");
        }
    }

    private Uri ValidateApiAssetUri(string value)
    {
        Uri uri = ValidateDownloadUri(value);
        string[] segments = GetDecodedPathSegments(uri);
        if (!IsGitHubApiHost(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || segments.Length != 6
            || !string.Equals(segments[0], "repos", StringComparison.Ordinal)
            || !string.Equals(segments[1], _settings.GitHubOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], _settings.GitHubRepository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[3], "releases", StringComparison.Ordinal)
            || !string.Equals(segments[4], "assets", StringComparison.Ordinal)
            || !long.TryParse(segments[5], out long assetId)
            || assetId <= 0)
        {
            throw new InvalidDataException("GitHub API asset URL is outside the configured repository.");
        }

        return uri;
    }

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException("Release asset URL is not an absolute URI.");
        }

        ValidateBaseUri(uri);
        if (!IsTrustedGitHubHost(uri.Host))
        {
            throw new InvalidDataException("Release asset URL is not a trusted GitHub host.");
        }

        return uri;
    }

    private static Uri ValidateAssetRedirect(Uri uri)
    {
        Uri validated = ValidateDownloadUri(uri.ToString());
        if (!IsReleaseAssetCdnHost(validated.Host))
        {
            throw new InvalidDataException("GitHub asset redirects must go directly to a trusted release CDN.");
        }

        return validated;
    }

    private static void ValidateBaseUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("GitHub URLs must use default-port HTTPS without user information or fragments.");
        }
    }

    private void ValidateReleaseTag(string releaseTag, string manifestVersion)
    {
        string expectedTag = "v" + manifestVersion;
        if (!string.Equals(releaseTag, expectedTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release tag {releaseTag} does not match manifest version {manifestVersion}.");
        }
    }

    private Uri BuildWebUri(params string[] remainingSegments) =>
        BuildGitHubUri(_settings.GitHubOwner, _settings.GitHubRepository, remainingSegments);

    private Uri BuildReleasePageUri(string releaseTag) =>
        BuildWebUri("releases", "tag", releaseTag);

    private Uri BuildReleaseAssetUri(string releaseTag, string assetName) =>
        BuildWebUri("releases", "download", releaseTag, assetName);

    private static Uri BuildGitHubUri(string owner, string repository, params string[] remainingSegments)
    {
        IEnumerable<string> segments = new[] { owner, repository }
            .Concat(remainingSegments)
            .Select(Uri.EscapeDataString);
        return new Uri("https://github.com/" + string.Join('/', segments));
    }

    private static string[] GetDecodedPathSegments(Uri uri) =>
        uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsGitHubApiHost(string host) =>
        string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubWebHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsReleaseAssetCdnHost(string host) =>
        string.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedGitHubHost(string host) =>
        IsGitHubApiHost(host) || IsGitHubWebHost(host) || IsReleaseAssetCdnHost(host);
}
