using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loopstructor.AutoPlayer.Updater.Models;

namespace Loopstructor.AutoPlayer.Updater.Services;

public sealed class GitHubReleaseClient
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumDeltaAssets = 16;
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
        await DownloadVerifiedPackageAsync(update, destinationPath, progress: null, cancellationToken);
    }

    public async Task DownloadVerifiedPackageAsync(
        ResolvedUpdate update,
        string destinationPath,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateResolvedPackage(update);
        await DownloadVerifiedAssetAsync(
            update.PackageAsset,
            update.Manifest.Sha256,
            update.Manifest.Size,
            destinationPath,
            progress,
            "下载安装包",
            cancellationToken);
    }

    public async Task DownloadVerifiedDeltaPackageAsync(
        ResolvedUpdate update,
        ResolvedDeltaPackage delta,
        string destinationPath,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(delta);
        ValidateResolvedDeltaPackage(update, delta);
        await DownloadVerifiedAssetAsync(
            delta.PackageAsset,
            delta.Manifest.Sha256,
            delta.Manifest.Size,
            destinationPath,
            progress,
            "下载增量更新包",
            cancellationToken);
    }

    private async Task DownloadVerifiedAssetAsync(
        GitHubReleaseAsset asset,
        string expectedSha256,
        long expectedSize,
        string destinationPath,
        IProgress<PackageDownloadProgress>? progress,
        string operation,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException("安装包目标文件已存在：" + destinationPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using HttpResponseMessage response = await SendDownloadWithRedirectsAsync(
            asset.DownloadUri,
            operation,
            cancellationToken);
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedSize)
        {
            throw new InvalidDataException($"下载内容长度 {contentLength} 与清单大小 {expectedSize} 不一致。");
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
        long lastReportedBytes = 0;
        TimeSpan lastReportedAt = TimeSpan.Zero;
        double smoothedBytesPerSecond = 0;
        Stopwatch downloadClock = Stopwatch.StartNew();
        ReportProgressSafely(progress, new PackageDownloadProgress(0, expectedSize, 0));
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > expectedSize || total > MaximumPackageBytes)
                {
                    throw new InvalidDataException("下载的安装包超过清单声明的大小。");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                TimeSpan elapsed = downloadClock.Elapsed;
                TimeSpan sampleDuration = elapsed - lastReportedAt;
                bool completed = total == expectedSize;
                if (completed || sampleDuration >= TimeSpan.FromMilliseconds(200))
                {
                    double seconds = Math.Max(sampleDuration.TotalSeconds, 0.001d);
                    double sampleSpeed = (total - lastReportedBytes) / seconds;
                    smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                        ? sampleSpeed
                        : (smoothedBytesPerSecond * 0.7d) + (sampleSpeed * 0.3d);
                    ReportProgressSafely(
                        progress,
                        new PackageDownloadProgress(total, expectedSize, smoothedBytesPerSecond));
                    lastReportedBytes = total;
                    lastReportedAt = elapsed;
                }
            }

            await output.FlushAsync(cancellationToken);
            if (total != expectedSize)
            {
                throw new InvalidDataException($"实际下载 {total} 字节，预期为 {expectedSize} 字节。");
            }

            byte[] actual = hash.GetHashAndReset();
            byte[] expected = Convert.FromHexString(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException("下载的安装包 SHA-256 与发布清单不一致。");
            }

            if (lastReportedBytes != total)
            {
                double averageSpeed = total / Math.Max(downloadClock.Elapsed.TotalSeconds, 0.001d);
                ReportProgressSafely(
                    progress,
                    new PackageDownloadProgress(total, expectedSize, averageSpeed));
            }
        }
        catch
        {
            await output.DisposeAsync();
            try { File.Delete(destinationPath); } catch { }
            throw;
        }
    }

    private static void ReportProgressSafely(
        IProgress<PackageDownloadProgress>? progress,
        PackageDownloadProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch
        {
            // Progress display is non-authoritative and must never interrupt a verified download.
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
            throw new InvalidOperationException("配置的 GitHub 仓库中没有找到最新 Release。");
        }

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
        {
            EnsureGitHubSuccess(response, "查询最新版本", credentialsSent: false);
            throw new InvalidDataException("GitHub 最新 Release 未跳转到带版本标签的 Release。");
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
            DeltaPackages = manifest.DeltaAssets
                .Select(delta =>
                {
                    Uri deltaUri = BuildReleaseAssetUri(releaseTag, delta.AssetName);
                    ValidateReleaseAssetUri(deltaUri, releaseTag, delta.AssetName);
                    return new ResolvedDeltaPackage
                    {
                        Manifest = delta,
                        PackageAsset = new GitHubReleaseAsset
                        {
                            Name = delta.AssetName,
                            DownloadUri = deltaUri,
                            Size = delta.Size
                        }
                    };
                })
                .ToArray(),
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
            throw new InvalidOperationException("配置的 GitHub 仓库中没有找到最新 Release。");
        }

        EnsureGitHubSuccess(response, "查询最新版本", credentialsSent: true);
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        GitHubReleaseResponse? release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            responseStream,
            JsonOptions,
            cancellationToken);
        if (release == null)
        {
            throw new InvalidDataException("GitHub 返回的 Release 响应为空。");
        }

        GitHubAssetResponse manifestAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, _settings.ManifestAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Release 缺少 " + _settings.ManifestAssetName + "。");
        if (manifestAsset.Size is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("GitHub 清单资源的大小超出允许范围。");
        }

        Uri manifestUri = ValidateApiAssetUri(manifestAsset.ApiUrl);
        byte[] manifestBytes = await DownloadSmallAssetAsync(manifestUri, cancellationToken);
        if (manifestBytes.LongLength != manifestAsset.Size)
        {
            throw new InvalidDataException(
                $"GitHub 清单资源大小 {manifestAsset.Size} 与实际下载大小 {manifestBytes.LongLength} 不一致。");
        }

        UpdateManifest manifest = DeserializeAndValidateManifest(manifestBytes);
        ValidateReleaseTag(release.TagName, manifest.Version);
        GitHubAssetResponse package = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, manifest.AssetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Release 不包含清单指定的安装包资源 " + manifest.AssetName + "。");
        if (package.Size != manifest.Size)
        {
            throw new InvalidDataException($"GitHub 资源大小 {package.Size} 与清单大小 {manifest.Size} 不一致。");
        }

        List<ResolvedDeltaPackage> deltaPackages = new();
        foreach (UpdateDeltaAsset delta in manifest.DeltaAssets)
        {
            GitHubAssetResponse? deltaAsset = release.Assets.SingleOrDefault(asset =>
                string.Equals(asset.Name, delta.AssetName, StringComparison.Ordinal));
            if (deltaAsset is null || deltaAsset.Size != delta.Size)
            {
                // A missing optional delta must not prevent older or skipped versions from using the full package.
                continue;
            }

            deltaPackages.Add(new ResolvedDeltaPackage
            {
                Manifest = delta,
                PackageAsset = new GitHubReleaseAsset
                {
                    Name = deltaAsset.Name,
                    DownloadUri = ValidateApiAssetUri(deltaAsset.ApiUrl),
                    Size = deltaAsset.Size
                }
            });
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
            DeltaPackages = deltaPackages,
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
            throw new InvalidDataException("更新清单为空。");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    private async Task<byte[]> DownloadSmallAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendDownloadWithRedirectsAsync(
            uri,
            "下载更新清单",
            cancellationToken);
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
        {
            throw new InvalidDataException("更新清单大小异常。");
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
                throw new InvalidDataException("更新清单超过大小限制。");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendDownloadWithRedirectsAsync(
        Uri initialUri,
        string operation,
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

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    EnsureGitHubSuccess(
                        response,
                        operation,
                        credentialsSent: includeToken && !string.IsNullOrWhiteSpace(_token));
                }
                finally
                {
                    response.Dispose();
                }
            }

            return response;
        }

        throw new HttpRequestException("GitHub 下载超过允许的重定向次数。");
    }

    private static void EnsureGitHubSuccess(
        HttpResponseMessage response,
        string operation,
        bool credentialsSent)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int statusCode = (int)response.StatusCode;
        string message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden =>
                $"{operation}失败：GitHub 返回 HTTP 403，访问被拒绝或已触发频率限制。" +
                "请稍后重试；如果配置了 GitHub Token，请确认其有效且拥有仓库访问权限。",
            HttpStatusCode.TooManyRequests =>
                $"{operation}失败：GitHub 返回 HTTP 429，已触发访问频率限制，请稍后重试。",
            HttpStatusCode.Unauthorized when credentialsSent =>
                $"{operation}失败：GitHub 返回 HTTP 401，请检查 GitHub Token 是否有效。",
            HttpStatusCode.Unauthorized =>
                $"{operation}失败：GitHub 返回 HTTP 401，身份验证失败。请确认仓库公开且当前链接可以访问。",
            _ => $"{operation}失败：GitHub 返回 HTTP {statusCode}。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
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
                throw new InvalidOperationException("GitHub 凭据只能发送到 api.github.com。");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    private void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != UpdateManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException("不支持的更新清单协议版本：" + manifest.SchemaVersion);
        }

        if (string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Version.Length > 128
            || !CanonicalSemanticVersionPattern.IsMatch(manifest.Version)
            || !SemanticVersion.TryParse(manifest.Version, out _))
        {
            throw new InvalidDataException("清单版本不是规范的 SemVer。");
        }

        if (!string.Equals(manifest.RuntimeIdentifier, "win-x64", StringComparison.Ordinal))
        {
            throw new InvalidDataException("清单中的运行时标识与此更新器不匹配。");
        }

        string expectedAssetName = $"Loopstructor-2-QA-Tool-{manifest.Version}-win-x64.zip";
        if (string.IsNullOrWhiteSpace(manifest.AssetName)
            || !string.Equals(Path.GetFileName(manifest.AssetName), manifest.AssetName, StringComparison.Ordinal)
            || !string.Equals(manifest.AssetName, expectedAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("清单中的安装包资源名称与其版本和运行时不匹配。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("清单中的 SHA-256 必须正好包含 64 个十六进制字符。");
        }

        if (manifest.Size <= 0 || manifest.Size > MaximumPackageBytes)
        {
            throw new InvalidDataException("清单中的安装包大小超出允许范围。");
        }

        manifest.Sha256 = manifest.Sha256.ToLowerInvariant();
        manifest.DeltaAssets ??= new List<UpdateDeltaAsset>();
        if (manifest.DeltaAssets.Count > MaximumDeltaAssets)
        {
            throw new InvalidDataException("清单中的增量更新包数量超出允许范围。");
        }

        SemanticVersion.TryParse(manifest.Version, out SemanticVersion? targetVersion);
        HashSet<string> sourceVersions = new(StringComparer.Ordinal);
        HashSet<string> deltaNames = new(StringComparer.Ordinal);
        foreach (UpdateDeltaAsset delta in manifest.DeltaAssets)
        {
            if (string.IsNullOrWhiteSpace(delta.FromVersion)
                || delta.FromVersion.Length > 128
                || !CanonicalSemanticVersionPattern.IsMatch(delta.FromVersion)
                || !SemanticVersion.TryParse(delta.FromVersion, out SemanticVersion? fromVersion)
                || fromVersion!.CompareTo(targetVersion) >= 0)
            {
                throw new InvalidDataException("增量更新包的起始版本不是早于目标版本的规范 SemVer。");
            }

            string expectedDeltaName =
                $"Loopstructor-2-QA-Tool-{delta.FromVersion}-to-{manifest.Version}-win-x64.delta.zip";
            if (!string.Equals(Path.GetFileName(delta.AssetName), delta.AssetName, StringComparison.Ordinal)
                || !string.Equals(delta.AssetName, expectedDeltaName, StringComparison.Ordinal)
                || !sourceVersions.Add(delta.FromVersion)
                || !deltaNames.Add(delta.AssetName))
            {
                throw new InvalidDataException("增量更新包名称、起始版本或唯一性无效。");
            }

            if (string.IsNullOrWhiteSpace(delta.Sha256)
                || delta.Sha256.Length != 64
                || delta.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("增量更新包 SHA-256 必须正好包含 64 个十六进制字符。");
            }

            if (delta.Size <= 0 || delta.Size >= manifest.Size || delta.Size > MaximumPackageBytes)
            {
                throw new InvalidDataException("增量更新包必须小于完整安装包且大小处于允许范围内。");
            }

            delta.Sha256 = delta.Sha256.ToLowerInvariant();
        }
    }

    private void ValidateResolvedPackage(ResolvedUpdate update)
    {
        ValidateManifest(update.Manifest);
        ValidateReleaseTag(update.ReleaseTag, update.Manifest.Version);
        if (!string.Equals(update.PackageAsset.Name, update.Manifest.AssetName, StringComparison.Ordinal)
            || update.PackageAsset.Size != update.Manifest.Size)
        {
            throw new InvalidDataException("解析出的安装包元数据与发布清单不匹配。");
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
            throw new InvalidDataException("解析出的安装包 URL 必须指向 GitHub，不能直接指向 Release CDN。");
        }
    }

    private void ValidateResolvedDeltaPackage(ResolvedUpdate update, ResolvedDeltaPackage delta)
    {
        ValidateManifest(update.Manifest);
        ValidateReleaseTag(update.ReleaseTag, update.Manifest.Version);
        UpdateDeltaAsset manifestDelta = update.Manifest.DeltaAssets.SingleOrDefault(candidate =>
            string.Equals(candidate.FromVersion, delta.Manifest.FromVersion, StringComparison.Ordinal)
            && string.Equals(candidate.AssetName, delta.Manifest.AssetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("解析出的增量更新包不属于当前发布清单。");
        if (!string.Equals(manifestDelta.Sha256, delta.Manifest.Sha256, StringComparison.Ordinal)
            || manifestDelta.Size != delta.Manifest.Size
            || !string.Equals(delta.PackageAsset.Name, delta.Manifest.AssetName, StringComparison.Ordinal)
            || delta.PackageAsset.Size != delta.Manifest.Size)
        {
            throw new InvalidDataException("解析出的增量更新包元数据与发布清单不匹配。");
        }

        if (IsGitHubWebHost(delta.PackageAsset.DownloadUri.Host))
        {
            ValidateReleaseAssetUri(delta.PackageAsset.DownloadUri, update.ReleaseTag, delta.Manifest.AssetName);
        }
        else if (IsGitHubApiHost(delta.PackageAsset.DownloadUri.Host))
        {
            ValidateApiAssetUri(delta.PackageAsset.DownloadUri.ToString());
        }
        else
        {
            throw new InvalidDataException("解析出的增量更新包 URL 必须指向 GitHub，不能直接指向 Release CDN。");
        }
    }

    private string ValidateReleasePageUri(Uri uri)
    {
        ValidateBaseUri(uri);
        if (!IsGitHubWebHost(uri.Host) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidDataException("最新 Release 跳转到了配置的 GitHub 仓库之外。");
        }

        string[] segments = GetDecodedPathSegments(uri);
        if (segments.Length != 5
            || !string.Equals(segments[0], _settings.GitHubOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], _settings.GitHubRepository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "releases", StringComparison.Ordinal)
            || !string.Equals(segments[3], "tag", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(segments[4]))
        {
            throw new InvalidDataException("最新 Release 跳转到了配置的 GitHub 仓库之外。");
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
            throw new InvalidDataException("Release 资源 URL 不属于配置的仓库和 Release。");
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
            throw new InvalidDataException("GitHub API 资源 URL 不属于配置的仓库。");
        }

        return uri;
    }

    private static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException("Release 资源 URL 不是绝对 URI。");
        }

        ValidateBaseUri(uri);
        if (!IsTrustedGitHubHost(uri.Host))
        {
            throw new InvalidDataException("Release 资源 URL 不属于受信任的 GitHub 主机。");
        }

        return uri;
    }

    private static Uri ValidateAssetRedirect(Uri uri)
    {
        Uri validated = ValidateDownloadUri(uri.ToString());
        if (!IsReleaseAssetCdnHost(validated.Host))
        {
            throw new InvalidDataException("GitHub 资源必须直接跳转到受信任的 Release CDN。");
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
            throw new InvalidDataException("GitHub URL 必须使用默认端口的 HTTPS，且不能包含用户信息或片段。");
        }
    }

    private void ValidateReleaseTag(string releaseTag, string manifestVersion)
    {
        string expectedTag = "v" + manifestVersion;
        if (!string.Equals(releaseTag, expectedTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release 标签 {releaseTag} 与清单版本 {manifestVersion} 不一致。");
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
