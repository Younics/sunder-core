using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace Sunder.App.Services;

public static class PackageIconImageLoader
{
    private static readonly HttpClient ImageHttpClient = new();

    public static async Task<PackageIconImageLoadResult> LoadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var format = ResolveIconFormat(response.Content.Headers.ContentType?.MediaType);
            if (format == PackageIconImageFormat.Unsupported)
            {
                var error = $"Unsupported package icon content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}' for '{uri}'.";
                AppSessionLog.WriteError(error);
                return PackageIconImageLoadResult.Failed(error);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
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
