using System.Text;
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

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            if (ShouldLoadAsSvg(uri, memory))
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

    private static bool ShouldLoadAsSvg(Uri uri, Stream stream)
    {
        return ResolveIconFormat(uri) switch
        {
            PackageIconImageFormat.Svg => true,
            PackageIconImageFormat.Raster => false,
            _ => HasSvgHeader(stream),
        };
    }

    private static PackageIconImageFormat ResolveIconFormat(Uri uri)
    {
        return Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).ToLowerInvariant() switch
        {
            ".svg" or ".svgz" => PackageIconImageFormat.Svg,
            ".bmp" or ".gif" or ".ico" or ".jpg" or ".jpeg" or ".png" or ".webp" => PackageIconImageFormat.Raster,
            _ => PackageIconImageFormat.Unknown,
        };
    }

    private static bool HasSvgHeader(Stream stream)
    {
        if (!stream.CanSeek || stream.Length == 0)
        {
            return false;
        }

        var originalPosition = stream.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[(int)Math.Min(512, stream.Length)];
            var bytesRead = stream.Read(buffer);
            var header = Encoding.UTF8.GetString(buffer[..bytesRead]);
            return header.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private enum PackageIconImageFormat
    {
        Unknown,
        Raster,
        Svg,
    }
}

public sealed record PackageIconImageLoadResult(IImage? Image, string? Error)
{
    public static PackageIconImageLoadResult Success(IImage image) => new(image, null);

    public static PackageIconImageLoadResult Failed(string error) => new(null, error);
}
