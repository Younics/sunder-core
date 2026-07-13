using System.Text.Json;

namespace Sunder.Cli;

internal static class CliHttpContentReader
{
    public static async Task<T?> ReadJsonAsync<T>(
        HttpContent content,
        long maxBytes,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        await CopyToAsync(content, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        return destination.Length == 0
            ? default
            : JsonSerializer.Deserialize<T>(destination.ToArray(), options);
    }

    public static async Task CopyToAsync(
        HttpContent content,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"HTTP content exceeds the {maxBytes} byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"HTTP content exceeds the {maxBytes} byte limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
