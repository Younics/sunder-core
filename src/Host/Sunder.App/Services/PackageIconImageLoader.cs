using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace Sunder.App.Services;

public static class PackageIconImageLoader
{
    private const long MaxIconBytes = 1_048_576;
    private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim ImageLoadSemaphore = new(4, 4);

    public static async Task<PackageIconImageLoadResult> LoadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var semaphoreAcquired = false;
        try
        {
            await ImageLoadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreAcquired = true;
            using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxIconBytes)
            {
                var error = $"Package icon '{uri}' exceeds the {MaxIconBytes} byte limit.";
                AppSessionLog.WriteError(error);
                return PackageIconImageLoadResult.Failed(error);
            }

            var format = ResolveIconFormat(response.Content.Headers.ContentType?.MediaType);
            if (format == PackageIconImageFormat.Unsupported)
            {
                var error = $"Unsupported package icon content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}' for '{uri}'.";
                AppSessionLog.WriteError(error);
                return PackageIconImageLoadResult.Failed(error);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var memory = await ReadBoundedContentAsync(source, MaxIconBytes, cancellationToken).ConfigureAwait(false);
            memory.Position = 0;

            if (format == PackageIconImageFormat.Svg)
            {
                return PackageIconImageLoadResult.Success(new SvgImage
                {
                    Source = SvgSource.LoadFromStream(memory),
                });
            }

            return PackageIconImageLoadResult.Success(new Bitmap(memory));
        }
        catch (Exception ex)
        {
            var error = $"Failed to load package icon '{uri}': {ex.Message}";
            AppSessionLog.WriteError(error, ex);
            return PackageIconImageLoadResult.Failed(error);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                ImageLoadSemaphore.Release();
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
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return memory;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                memory.Dispose();
                throw new InvalidOperationException($"Image content exceeds the {maxBytes} byte limit.");
            }

            memory.Write(buffer, 0, bytesRead);
        }
    }

    internal static PackageIconImageFormat ResolveIconFormat(string? contentType)
    {
        if (string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return PackageIconImageFormat.Svg;
        }

        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return PackageIconImageFormat.Raster;
        }

        return PackageIconImageFormat.Unsupported;
    }

    internal enum PackageIconImageFormat
    {
        Unsupported,
        Raster,
        Svg,
    }
}

public sealed record PackageIconImageLoadResult(IImage? Image, string? Error)
{
    public static PackageIconImageLoadResult Success(IImage image) => new(image, null);

    public static PackageIconImageLoadResult Failed(string error) => new(null, error);
}
