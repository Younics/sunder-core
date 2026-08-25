using System.Text;
using System.Text.Json;

namespace Sunder.App.Services;

internal static class BoundedHttpContentReader
{
    public static async Task<T?> ReadJsonAsync<T>(
        HttpContent content,
        long maxBytes,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? default : JsonSerializer.Deserialize<T>(bytes, options);
    }

    public static async Task<string> ReadStringAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await ReadBytesAsync(content, maxBytes, cancellationToken).ConfigureAwait(false));

    public static async Task CopyToAsync(
        HttpContent content,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"HTTP content exceeds the {maxBytes} byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
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
                return;
            }

            if (read > remaining)
            {
                throw new InvalidDataException($"HTTP content exceeds the {maxBytes} byte limit.");
            }

            total += read;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        await CopyToAsync(content, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }
}
