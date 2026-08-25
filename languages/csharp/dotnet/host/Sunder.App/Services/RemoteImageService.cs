using System.Net;
using System.Net.Http.Headers;
using Avalonia.Media;
using Sunder.Package.Format;

namespace Sunder.App.Services;

public sealed class RemoteImageService : IDisposable
{
    internal const int MaxAcceptedMediaEntries = 12;
    internal const long MaxRegistryMediaBytes = 8L * 1024 * 1024;
    internal const long MaxAvatarBytes = 2L * 1024 * 1024;
    private const int MaxRedirects = 3;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly RemoteImageDisplaySize IconDisplaySize = new(128, 128);
    private static readonly RemoteImageDisplaySize MediaDisplaySize = new(1120, 760);
    private static readonly RemoteImageDisplaySize AvatarDisplaySize = new(84, 84);
    private readonly HttpClient _registryClient;
    private readonly HttpClient _externalAvatarClient;
    private readonly Func<byte[], RemoteImageMetadata, RemoteImageDisplaySize, IImage> _decode;
    private readonly SemaphoreSlim _requestGate = new(6, 6);
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    public RemoteImageService()
        : this(
            CreateRegistryHandler(),
            CreateExternalAvatarHandler(),
            RemoteImageDecoder.Decode,
            DefaultRequestTimeout)
    {
    }

    internal RemoteImageService(
        HttpMessageHandler registryHandler,
        HttpMessageHandler externalAvatarHandler,
        Func<byte[], RemoteImageMetadata, RemoteImageDisplaySize, IImage> decode,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registryHandler);
        ArgumentNullException.ThrowIfNull(externalAvatarHandler);
        ArgumentNullException.ThrowIfNull(decode);
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _registryClient = CreateHttpClient(registryHandler);
        _externalAvatarClient = CreateHttpClient(externalAvatarHandler);
        _decode = decode;
    }

    public Task<RemoteImageLoadResult> LoadRegistryIconAsync(
        Uri registryOrigin,
        Uri imageUri,
        CancellationToken cancellationToken = default)
        => LoadAsync(
            registryOrigin,
            imageUri,
            RemoteImagePolicy.Registry,
            SunderPackageFormat.MaxIconBytes,
            IconDisplaySize,
            cancellationToken);

    public Task<RemoteImageLoadResult> LoadRegistryMediaAsync(
        Uri registryOrigin,
        Uri imageUri,
        CancellationToken cancellationToken = default)
        => LoadAsync(
            registryOrigin,
            imageUri,
            RemoteImagePolicy.Registry,
            MaxRegistryMediaBytes,
            MediaDisplaySize,
            cancellationToken);

    public Task<RemoteImageLoadResult> LoadAvatarAsync(
        Uri registryOrigin,
        Uri imageUri,
        CancellationToken cancellationToken = default)
        => LoadAsync(
            registryOrigin,
            imageUri,
            RemoteImagePolicy.Avatar,
            MaxAvatarBytes,
            AvatarDisplaySize,
            cancellationToken);

    internal static bool TryResolveRegistryImageUri(
        Uri registryOrigin,
        string? value,
        out Uri imageUri)
    {
        imageUri = null!;
        if (!HttpMediaUriValidator.IsValid(registryOrigin) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var candidate)
            && !Uri.TryCreate(registryOrigin, normalized, out candidate))
        {
            return false;
        }

        if (!HttpMediaUriValidator.HasSameOrigin(registryOrigin, candidate))
        {
            return false;
        }

        imageUri = candidate;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registryClient.Dispose();
        _externalAvatarClient.Dispose();
        _requestGate.Dispose();
    }

    private async Task<RemoteImageLoadResult> LoadAsync(
        Uri registryOrigin,
        Uri imageUri,
        RemoteImagePolicy policy,
        long maxBytes,
        RemoteImageDisplaySize displaySize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedRequestUri(registryOrigin, imageUri, policy))
        {
            return RemoteImageLoadResult.Failed("Image URL is outside the allowed origin or network policy.");
        }

        var acquired = false;
        try
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            using var timeout = _requestTimeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(_requestTimeout);
            using var linked = timeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var effectiveToken = linked?.Token ?? cancellationToken;
            try
            {
                using var response = await SendWithRedirectsAsync(
                    registryOrigin,
                    imageUri,
                    policy,
                    effectiveToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentEncoding.Count > 0)
                {
                    return RemoteImageLoadResult.Failed("Compressed HTTP image responses are not accepted.");
                }
                if (response.Content.Headers.ContentLength is { } contentLength
                    && (contentLength < 0 || contentLength > maxBytes))
                {
                    return RemoteImageLoadResult.Failed($"Image exceeds the {maxBytes} byte limit.");
                }

                await using var source = await response.Content
                    .ReadAsStreamAsync(effectiveToken)
                    .ConfigureAwait(false);
                var content = await ReadBoundedAsync(source, maxBytes, effectiveToken).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!RemoteImageDecoder.TryReadMetadata(content, contentType, out var metadata))
                {
                    return RemoteImageLoadResult.Failed(
                        "Image format or decoded dimensions exceed the allowed limits.");
                }

                return RemoteImageLoadResult.Success(_decode(content, metadata, displaySize));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
            {
                return RemoteImageLoadResult.Failed($"Image request exceeded the {_requestTimeout} timeout.");
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError(
                    $"Remote image request to '{imageUri.Host}' failed: {exception.Message}",
                    exception,
                    visibleInDeveloperLog: false);
                return RemoteImageLoadResult.Failed("Image could not be loaded safely.");
            }
        }
        finally
        {
            if (acquired)
            {
                _requestGate.Release();
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri registryOrigin,
        Uri initialUri,
        RemoteImagePolicy policy,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var redirectCount = 0; ; redirectCount++)
        {
            if (!IsAllowedRequestUri(registryOrigin, currentUri, policy))
            {
                throw new InvalidDataException("Image redirect violates the allowed origin or network policy.");
            }
            if (!visited.Add(currentUri.AbsoluteUri))
            {
                throw new InvalidDataException("Image redirect loop detected.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var client = HttpMediaUriValidator.HasSameOrigin(registryOrigin, currentUri)
                ? _registryClient
                : _externalAvatarClient;
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is not { } responseUri
                || !currentUri.Equals(responseUri))
            {
                response.Dispose();
                throw new InvalidDataException("Image transport changed the request URL unexpectedly.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirectCount >= MaxRedirects)
            {
                throw new InvalidDataException("Image redirect policy was exceeded.");
            }

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Image redirects may not downgrade HTTPS.");
            }
            currentUri = nextUri;
        }
    }

    private static bool IsAllowedRequestUri(
        Uri registryOrigin,
        Uri uri,
        RemoteImagePolicy policy)
    {
        if (!HttpMediaUriValidator.IsValid(registryOrigin) || !HttpMediaUriValidator.IsValid(uri))
        {
            return false;
        }
        if (HttpMediaUriValidator.HasSameOrigin(registryOrigin, uri))
        {
            return true;
        }

        return policy == RemoteImagePolicy.Avatar
               && ExternalImageNetworkGuard.IsAllowedExternalUri(uri);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var remaining = maxBytes - total;
            var readLength = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await source.ReadAsync(
                buffer.AsMemory(0, readLength),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return content.ToArray();
            }
            if (read > remaining)
            {
                throw new InvalidDataException($"Image exceeds the {maxBytes} byte limit.");
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sunder.App/1.0");
        return client;
    }

    private static HttpMessageHandler CreateRegistryHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 6,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
        };

    private static HttpMessageHandler CreateExternalAvatarHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ExternalImageNetworkGuard.ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = false,
        };

    private enum RemoteImagePolicy
    {
        Registry,
        Avatar,
    }
}

public sealed record RemoteImageLoadResult(IImage? Image, string? Error)
{
    public static RemoteImageLoadResult Success(IImage image) => new(image, null);

    public static RemoteImageLoadResult Failed(string error) => new(null, error);
}
