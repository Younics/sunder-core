using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class BoundedImageContentLoader
{
    public static async Task<BoundedImageContentLoadResult> LoadAsync(
        HttpClient httpClient,
        SemaphoreSlim loadSemaphore,
        Uri uri,
        long maxBytes,
        CancellationToken cancellationToken)
        => await LoadAsync(
            (requestUri, token) => httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            loadSemaphore,
            uri,
            maxBytes,
            httpClient.Timeout,
            cancellationToken).ConfigureAwait(false);

    public static async Task<BoundedImageContentLoadResult> LoadAsync(
        RuntimeClientTransport transport,
        SemaphoreSlim loadSemaphore,
        Uri uri,
        long maxBytes,
        CancellationToken cancellationToken)
        => await LoadAsync(
            (requestUri, token) => transport.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            loadSemaphore,
            uri,
            maxBytes,
            requestTimeout: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private static async Task<BoundedImageContentLoadResult> LoadAsync(
        Func<Uri, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        SemaphoreSlim loadSemaphore,
        Uri uri,
        long maxBytes,
        TimeSpan? requestTimeout,
        CancellationToken cancellationToken)
    {
        if (!HttpMediaUriValidator.IsValid(uri))
        {
            return BoundedImageContentLoadResult.Failed($"Image URL '{uri}' must use HTTP or HTTPS without user information.");
        }

        var semaphoreAcquired = false;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;
        try
        {
            await loadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreAcquired = true;
            var effectiveToken = cancellationToken;
            if (requestTimeout is { } duration && duration != Timeout.InfiniteTimeSpan)
            {
                timeout = new CancellationTokenSource(duration);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                effectiveToken = linked.Token;
            }

            using var response = await sendAsync(uri, effectiveToken).ConfigureAwait(false);
            if (!HttpMediaUriValidator.HasSameOrigin(uri, response.RequestMessage?.RequestUri))
            {
                return BoundedImageContentLoadResult.Failed($"Image '{uri}' redirected to an untrusted origin.");
            }
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return BoundedImageContentLoadResult.Failed($"Image '{uri}' exceeds the {maxBytes} byte limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(effectiveToken).ConfigureAwait(false);
            var memory = await ReadBoundedContentAsync(source, maxBytes, effectiveToken).ConfigureAwait(false);
            memory.Position = 0;
            return BoundedImageContentLoadResult.Success(
                memory,
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentEncoding.ToArray());
        }
        catch (OperationCanceledException exception)
            when (timeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Image request exceeded the {requestTimeout} timeout.", exception);
        }
        finally
        {
            linked?.Dispose();
            timeout?.Dispose();
            if (semaphoreAcquired)
            {
                loadSemaphore.Release();
            }
        }
    }

    private static async Task<MemoryStream> ReadBoundedContentAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var remaining = maxBytes - totalBytes;
                var readLength = (int)Math.Min(buffer.Length, remaining + 1);
                var bytesRead = await source.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    memory.Position = 0;
                    return memory;
                }

                if (bytesRead > remaining)
                {
                    throw new InvalidOperationException($"Image content exceeds the {maxBytes} byte limit.");
                }

                totalBytes += bytesRead;
                memory.Write(buffer, 0, bytesRead);
            }
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }
}

internal sealed record BoundedImageContentLoadResult(
    MemoryStream? Content,
    string? ContentType,
    IReadOnlyList<string> ContentEncodings,
    string? Error)
{
    public static BoundedImageContentLoadResult Success(
        MemoryStream content,
        string? contentType,
        IReadOnlyList<string> contentEncodings)
        => new(content, contentType, contentEncodings, null);

    public static BoundedImageContentLoadResult Failed(string error)
        => new(null, null, [], error);
}
