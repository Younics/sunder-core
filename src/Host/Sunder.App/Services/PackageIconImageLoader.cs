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
        try
        {
            var download = await BoundedImageContentLoader
                .LoadAsync(ImageHttpClient, ImageLoadSemaphore, uri, MaxIconBytes, cancellationToken)
                .ConfigureAwait(false);
            if (download.Error is not null)
            {
                AppSessionLog.WriteError(download.Error);
                return PackageIconImageLoadResult.Failed(download.Error);
            }

            using var memory = download.Content ?? throw new InvalidOperationException("Image download completed without content.");
            var format = ResolveIconFormat(download.ContentType);
            if (format == PackageIconImageFormat.Unsupported)
            {
                var error = $"Unsupported package icon content type '{download.ContentType ?? "unknown"}' for '{uri}'.";
                AppSessionLog.WriteError(error);
                return PackageIconImageLoadResult.Failed(error);
            }

            if (format == PackageIconImageFormat.Svg)
            {
                return PackageIconImageLoadResult.Success(new SvgImage
                {
                    Source = SvgSource.LoadFromStream(memory),
                });
            }

            return PackageIconImageLoadResult.Success(new Bitmap(memory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"Failed to load package icon '{uri}': {ex.Message}";
            AppSessionLog.WriteError(error, ex);
            return PackageIconImageLoadResult.Failed(error);
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
