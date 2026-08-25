using Avalonia.Media;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal static class PackageIconImageViewModelLoader
{
    public static async Task LoadAsync(
        Uri iconUri,
        PackageIconTransport transport,
        Uri? registryOrigin,
        RemoteImageService? remoteImageService,
        IUiDispatcher uiDispatcher,
        Func<bool> isDisposed,
        Action<string, IImage?> applyResult,
        CancellationToken cancellationToken = default)
    {
        IImage? image;
        string? error;
        try
        {
            if (transport == PackageIconTransport.RuntimeAsset)
            {
                var result = await PackageIconImageLoader
                    .LoadRuntimeAssetAsync(iconUri, cancellationToken)
                    .ConfigureAwait(false);
                image = result.Image;
                error = result.Error;
            }
            else if (registryOrigin is not null && remoteImageService is not null)
            {
                var result = await remoteImageService
                    .LoadRegistryIconAsync(registryOrigin, iconUri, cancellationToken)
                    .ConfigureAwait(false);
                image = result.Image;
                error = result.Error;
            }
            else
            {
                image = null;
                error = "Registry image service is not available.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await uiDispatcher.InvokeAsync(() =>
        {
            if (isDisposed() || cancellationToken.IsCancellationRequested)
            {
                DisposeImage(image);
                return;
            }

            applyResult(error ?? string.Empty, image);
        });
    }

    public static void DisposeImage(IImage? image)
    {
        if (image is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
