using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class GitHubReleaseClient
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly UpdateSourceSettings _settings;
    private readonly string _token;

    public GitHubReleaseClient(HttpClient httpClient, UpdateSourceSettings settings, string? token = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _token = token ?? string.Empty;
    }

    public async Task<ResolvedUpdate> ResolveLatestAsync(CancellationToken cancellationToken = default)
    {
        Uri releaseUri = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(_settings.GitHubOwner)}/{Uri.EscapeDataString(_settings.GitHubRepository)}/releases/latest");
        using HttpRequestMessage request = CreateRequest(releaseUri, "application/vnd.github+json", includeToken: true);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        Uri manifestUri = ValidateDownloadUri(manifestAsset.BrowserDownloadUrl);
        byte[] manifestBytes = await DownloadSmallAssetAsync(manifestUri, cancellationToken);
        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions);
        if (manifest == null)
        {
            throw new InvalidDataException("Update manifest is empty.");
        }

        ValidateManifest(manifest);
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
                DownloadUri = ValidateDownloadUri(package.BrowserDownloadUrl),
                Size = package.Size
            },
            ReleaseTag = release.TagName,
            ReleasePageUrl = release.HtmlUrl
        };
    }

    public async Task DownloadVerifiedPackageAsync(
        ResolvedUpdate update,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
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

    private async Task<HttpResponseMessage> SendDownloadWithRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirect = 0; redirect <= 6; redirect++)
        {
            using HttpRequestMessage request = CreateRequest(
                current,
                "application/octet-stream",
                includeToken: IsGitHubApiOrWebHost(current.Host));
            HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location != null)
            {
                Uri next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                response.Dispose();
                current = ValidateDownloadUri(next.ToString());
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
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (includeToken && !string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    private void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported update manifest schema: " + manifest.SchemaVersion);
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            throw new InvalidDataException("Manifest version is not valid SemVer.");
        }

        if (!string.Equals(manifest.RuntimeIdentifier, _settings.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest runtime identifier does not match this updater.");
        }

        if (string.IsNullOrWhiteSpace(manifest.AssetName)
            || !string.Equals(Path.GetFileName(manifest.AssetName), manifest.AssetName, StringComparison.Ordinal)
            || !manifest.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest package asset name is invalid.");
        }

        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Manifest SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (manifest.Size <= 0 || manifest.Size > MaximumPackageBytes)
        {
            throw new InvalidDataException("Manifest package size is outside the accepted range.");
        }

        manifest.Sha256 = manifest.Sha256.ToLowerInvariant();
    }

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsTrustedGitHubHost(uri.Host))
        {
            throw new InvalidDataException("Release asset URL is not a trusted HTTPS GitHub host.");
        }

        return uri;
    }

    private static bool IsGitHubApiOrWebHost(string host) =>
        string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedGitHubHost(string host) =>
        IsGitHubApiOrWebHost(host)
        || string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
}
