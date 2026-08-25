using System.Net;
using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

public sealed class RegistryApiClient : IRegistryApiClient
{
    private const long MaxJsonResponseBytes = 4L * 1024 * 1024;
    private const long MaxStackDownloadBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultStackTransferTimeout = TimeSpan.FromMinutes(15);
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _stackTransferTimeout;
    private readonly Func<TimeSpan, CancellationTokenSource> _createTimeoutSource;

    public RegistryApiClient(Uri registryUrl, HttpClient? httpClient = null)
        : this(
            registryUrl,
            httpClient,
            DefaultRequestTimeout,
            DefaultStackTransferTimeout,
            createTimeoutSource: null)
    {
    }

    internal RegistryApiClient(Uri registryUrl, HttpClient? httpClient, TimeSpan requestTimeout)
        : this(
            registryUrl,
            httpClient,
            requestTimeout,
            DefaultStackTransferTimeout,
            createTimeoutSource: null)
    {
    }

    internal RegistryApiClient(
        Uri registryUrl,
        HttpClient? httpClient,
        TimeSpan requestTimeout,
        TimeSpan stackTransferTimeout,
        Func<TimeSpan, CancellationTokenSource>? createTimeoutSource)
    {
        if (requestTimeout <= TimeSpan.Zero && requestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        if (stackTransferTimeout <= TimeSpan.Zero && stackTransferTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(stackTransferTimeout));
        }
        RegistryUrl = RegistryUrlHelper.Normalize(registryUrl);
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = RegistryUrl,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _disposeHttpClient = httpClient is null;
        _requestTimeout = requestTimeout;
        _stackTransferTimeout = stackTransferTimeout;
        _createTimeoutSource = createTimeoutSource
                               ?? (static duration => new CancellationTokenSource(duration));
    }

    public Uri RegistryUrl { get; }

    public async Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Packages)}?skip={skip}&take={Math.Clamp(take, 1, 100)}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await GetJsonAsync<IReadOnlyList<RegistryPackageSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Package(packageId)),
            cancellationToken);

    public async Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, RegistrySearchSort sort = RegistrySearchSort.Downloads, CancellationToken cancellationToken = default)
    {
        var path = $"{RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Stacks)}?skip={skip}&take={Math.Clamp(take, 1, 100)}&sort={FormatSort(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return await GetJsonAsync<IReadOnlyList<RegistryStackSummary>>(path, cancellationToken) ?? [];
    }

    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryStackDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Stack(stackId)),
            cancellationToken);

    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
        => GetOrNullAsync<RegistryPackageVersionDetails>(
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackageVersion(packageId, version)),
            cancellationToken);

    public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken cancellationToken = default)
        => DownloadAsync(artifact.DownloadUrl, artifact.Size, artifact.Sha256, $"Stack '{stackId}'", destinationPath, cancellationToken);

    private async Task DownloadAsync(string url, long? size, string hash, string label, string destinationPath, CancellationToken cancellationToken)
    {
        if (size < 0 || size > MaxStackDownloadBytes)
        {
            throw new InvalidDataException($"Downloaded {label} exceeds the Stack download limit.");
        }

        var downloadUri = CreateDownloadUri(url);
        using var response = await ExecuteWithTimeoutAsync(
            token => GetAsync(downloadUri, token),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxStackDownloadBytes
            || size is { } expectedSize
            && response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedSize)
        {
            throw new InvalidDataException($"Downloaded {label} has an invalid content length.");
        }

        await ExecuteWithTimeoutAsync(async token =>
        {
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await VerifiedFileTransfer.PublishAsync(
                source,
                destinationPath,
                MaxStackDownloadBytes,
                size,
                hash,
                $"Downloaded {label}",
                token).ConfigureAwait(false);
        }, cancellationToken, _stackTransferTimeout, "Registry Stack transfer").ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
        => await ExecuteWithTimeoutAsync(async token =>
        {
            using var response = await GetAsync(CreateUri(path), token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            response.EnsureSuccessStatusCode();
            return await BoundedHttpContentReader.ReadJsonAsync<T>(
                response.Content,
                MaxJsonResponseBytes,
                JsonOptions,
                token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
        => await ExecuteWithTimeoutAsync(async token =>
        {
            using var response = await GetAsync(CreateUri(path), token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await BoundedHttpContentReader.ReadJsonAsync<T>(
                response.Content,
                MaxJsonResponseBytes,
                JsonOptions,
                token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!HttpMediaUriValidator.HasSameOrigin(RegistryUrl, uri))
        {
            throw new InvalidDataException("Registry request URL must remain on the trusted Registry origin.");
        }

        var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (HttpMediaUriValidator.HasSameOrigin(RegistryUrl, response.RequestMessage?.RequestUri))
        {
            return response;
        }

        response.Dispose();
        throw new InvalidDataException("Registry response changed to an untrusted origin.");
    }

    private async Task ExecuteWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
        => await ExecuteWithTimeoutAsync(
            operation,
            cancellationToken,
            _requestTimeout,
            "Registry request").ConfigureAwait(false);

    private async Task ExecuteWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        TimeSpan timeoutDuration,
        string operationName)
    {
        _ = await ExecuteWithTimeoutAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken, timeoutDuration, operationName).ConfigureAwait(false);
    }

    private async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => await ExecuteWithTimeoutAsync(
            operation,
            cancellationToken,
            _requestTimeout,
            "Registry request").ConfigureAwait(false);

    private async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        TimeSpan timeoutDuration,
        string operationName)
    {
        using var timeout = timeoutDuration == Timeout.InfiniteTimeSpan
            ? null
            : _createTimeoutSource(timeoutDuration);
        using var linked = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await operation(linked?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (timeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operationName} exceeded the {timeoutDuration} timeout.", exception);
        }
    }

    private Uri CreateUri(string path)
        => Uri.TryCreate(path, UriKind.Absolute, out var absolute)
           && (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? absolute
            : new Uri(RegistryUrl, path);

    private Uri CreateDownloadUri(string path)
    {
        var uri = CreateUri(path);
        if (!HttpMediaUriValidator.IsValid(uri)
            || !HttpMediaUriValidator.HasSameOrigin(RegistryUrl, uri))
        {
            throw new InvalidDataException("Registry download URL must remain on the trusted Registry origin.");
        }

        return uri;
    }
    private static string FormatSort(RegistrySearchSort sort) => sort switch { RegistrySearchSort.Downloads => "downloads", RegistrySearchSort.Stars => "stars", _ => "updated" };

    public void Dispose()
    {
        if (_disposeHttpClient) _httpClient.Dispose();
    }
}
