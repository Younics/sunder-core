namespace Sunder.App.Services;

internal static class BoundedImageContentLoader
{
    public static async Task<BoundedImageContentLoadResult> LoadAsync(
        HttpClient httpClient,
        SemaphoreSlim loadSemaphore,
        Uri uri,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var semaphoreAcquired = false;
        try
        {
            await loadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreAcquired = true;
            using var response = await httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return BoundedImageContentLoadResult.Failed($"Image '{uri}' exceeds the {maxBytes} byte limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var memory = await ReadBoundedContentAsync(source, maxBytes, cancellationToken).ConfigureAwait(false);
            memory.Position = 0;
            return BoundedImageContentLoadResult.Success(memory, response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
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
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    memory.Position = 0;
                    return memory;
                }

                totalBytes += bytesRead;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"Image content exceeds the {maxBytes} byte limit.");
                }

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
    string? Error)
{
    public static BoundedImageContentLoadResult Success(MemoryStream content, string? contentType)
        => new(content, contentType, null);

    public static BoundedImageContentLoadResult Failed(string error)
        => new(null, null, error);
}
