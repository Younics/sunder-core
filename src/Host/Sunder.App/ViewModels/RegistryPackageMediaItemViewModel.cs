using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageMediaItemViewModel : ViewModelBase, IDisposable
{
    private const long MaxMediaImageBytes = 8_388_608;
    private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim ImageLoadSemaphore = new(2, 2);
    private readonly Func<RegistryPackageMediaItemViewModel, Task> _onSelectAsync;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _loadTask;
    private bool _disposed;

    public RegistryPackageMediaItemViewModel(
        RegistryPackageMedia media,
        Func<RegistryPackageMediaItemViewModel, Task> onSelectAsync)
    {
        Url = media.Url;
        Caption = media.AltText ?? media.FileName;
        FileName = media.FileName;
        Size = media.Size;
        _onSelectAsync = onSelectAsync;
        _ = EnsureImageLoadedAsync();
    }

    public string Url { get; }

    public string Caption { get; }

    public string FileName { get; }

    public long Size { get; }

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isImageLoading;

    [RelayCommand]
    private async Task SelectAsync()
    {
        await EnsureImageLoadedAsync();
        await _onSelectAsync(this);
    }

    public async Task EnsureImageLoadedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }

        _loadTask ??= LoadImageAsync(_disposeCts.Token);
        await _loadTask.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        Image?.Dispose();
        Image = null;
        if (_loadTask?.IsCompleted == false)
        {
            _ = _loadTask.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    _disposeCts.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _disposeCts.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        await SetImageLoadingAsync(true);
        try
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            {
                await ClearImageAsync(cancellationToken);
                return;
            }

            var download = await BoundedImageContentLoader
                .LoadAsync(ImageHttpClient, ImageLoadSemaphore, uri, MaxMediaImageBytes, cancellationToken)
                .ConfigureAwait(false);
            if (download.Error is not null || download.Content is null)
            {
                await ClearImageAsync(cancellationToken);
                return;
            }

            using var memory = download.Content;
            bitmap = new Bitmap(memory);
            await ApplyLoadedBitmapAsync(bitmap, cancellationToken);
            bitmap = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await ClearImageAsync(cancellationToken);
        }
        finally
        {
            bitmap?.Dispose();
            await SetImageLoadingAsync(false);
        }
    }

    private Task ApplyLoadedBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        return UiThread.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            Image?.Dispose();
            Image = bitmap;
        });
    }

    private Task ClearImageAsync(CancellationToken cancellationToken)
    {
        return UiThread.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Image?.Dispose();
            Image = null;
        });
    }

    private Task SetImageLoadingAsync(bool value)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return UiThread.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                IsImageLoading = value;
            }
        });
    }
}
