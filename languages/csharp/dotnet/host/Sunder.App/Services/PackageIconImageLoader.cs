using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sunder.Runtime.Client;
using Sunder.Package.Format;

namespace Sunder.App.Services;

public static class PackageIconImageLoader
{
    private const long MaxIconBytes = SunderPackageFormat.MaxIconBytes;
    private static RuntimeClientTransport? _runtimeTransport;
    private static readonly HttpClient AnonymousMediaHttpClient = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private static readonly SemaphoreSlim RuntimeAssetLoadSemaphore = new(4, 4);
    private static readonly SemaphoreSlim AnonymousMediaLoadSemaphore = new(4, 4);

    public static async Task<PackageIconImageLoadResult> LoadAsync(Uri uri, CancellationToken cancellationToken = default)
        => await LoadRuntimeAssetAsync(uri, cancellationToken).ConfigureAwait(false);

    public static Task<PackageIconImageLoadResult> LoadRuntimeAssetAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = Volatile.Read(ref _runtimeTransport);
        if (transport is null)
        {
            return Task.FromResult(PackageIconImageLoadResult.Failed(
                "Authenticated Runtime image transport is not available."));
        }
        if (!HttpMediaUriValidator.IsRuntimeAsset(uri, transport.GetConnectionInfo()))
        {
            return Task.FromResult(PackageIconImageLoadResult.Failed(
                $"Package asset URL '{uri}' does not belong to the authenticated Runtime origin."));
        }

        return LoadCoreAsync(
            (loadSemaphore, requestUri, token) => BoundedImageContentLoader.LoadAsync(
                transport,
                loadSemaphore,
                requestUri,
                MaxIconBytes,
                token),
            RuntimeAssetLoadSemaphore,
            uri,
            cancellationToken);
    }

    public static Task<PackageIconImageLoadResult> LoadAnonymousMediaAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HttpMediaUriValidator.IsValid(uri))
        {
            return Task.FromResult(PackageIconImageLoadResult.Failed(
                $"Anonymous media URL '{uri}' must use HTTP or HTTPS without user information."));
        }

        return LoadCoreAsync(
            (loadSemaphore, requestUri, token) => BoundedImageContentLoader.LoadAsync(
                AnonymousMediaHttpClient,
                loadSemaphore,
                requestUri,
                MaxIconBytes,
                token),
            AnonymousMediaLoadSemaphore,
            uri,
            cancellationToken);
    }

    private static async Task<PackageIconImageLoadResult> LoadCoreAsync(
        Func<SemaphoreSlim, Uri, CancellationToken, Task<BoundedImageContentLoadResult>> loadAsync,
        SemaphoreSlim loadSemaphore,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            var download = await loadAsync(loadSemaphore, uri, cancellationToken).ConfigureAwait(false);
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

    internal static void ConfigureRuntimeTransport(RuntimeClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Volatile.Write(ref _runtimeTransport, transport);
    }

    internal static async Task<PackageIconImageLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxIconBytes)
            {
                var error = $"Package icon '{path}' is missing or exceeds the {MaxIconBytes} byte limit.";
                AppSessionLog.WriteError(error);
                return PackageIconImageLoadResult.Failed(error);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var format = ResolveIconFormatFromPath(path);
            if (format == PackageIconImageFormat.Raster)
            {
                return PackageIconImageLoadResult.Success(new Bitmap(stream));
            }

            var unsupportedError = $"Unsupported package icon file '{path}'.";
            AppSessionLog.WriteError(unsupportedError);
            return PackageIconImageLoadResult.Failed(unsupportedError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"Failed to load package icon '{path}': {ex.Message}";
            AppSessionLog.WriteError(error, ex);
            return PackageIconImageLoadResult.Failed(error);
        }
    }

    internal static PackageIconImageFormat ResolveIconFormat(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/x-icon" or "image/vnd.microsoft.icon"
                => PackageIconImageFormat.Raster,
            _ => PackageIconImageFormat.Unsupported,
        };
    }

    internal static PackageIconImageFormat ResolveIconFormatFromPath(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico" => PackageIconImageFormat.Raster,
            _ => PackageIconImageFormat.Unsupported,
        };

    internal enum PackageIconImageFormat
    {
        Unsupported,
        Raster,
    }
}

public enum PackageIconTransport
{
    RuntimeAsset,
    AnonymousMedia,
}

public sealed record PackageIconImageLoadResult(IImage? Image, string? Error)
{
    public static PackageIconImageLoadResult Success(IImage image) => new(image, null);

    public static PackageIconImageLoadResult Failed(string error) => new(null, error);
}
