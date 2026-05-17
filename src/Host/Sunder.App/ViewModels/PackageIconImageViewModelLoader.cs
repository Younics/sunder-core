using Avalonia.Media;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal static class PackageIconImageViewModelLoader
{
    public static async Task LoadAsync(
        Uri iconUri,
        Func<bool> isDisposed,
        Action<string, IImage?> applyResult,
        CancellationToken cancellationToken = default)
    {
        PackageIconImageLoadResult result;
        try
        {
            result = await PackageIconImageLoader.LoadAsync(iconUri, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await UiThread.InvokeAsync(() =>
        {
            if (isDisposed() || cancellationToken.IsCancellationRequested)
            {
                DisposeImage(result.Image);
                return;
            }

            applyResult(result.Error ?? string.Empty, result.Image);
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
