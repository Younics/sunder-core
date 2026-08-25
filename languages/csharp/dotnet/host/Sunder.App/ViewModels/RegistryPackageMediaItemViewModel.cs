using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageMediaItemViewModel : ViewModelBase, IDisposable
{
    private readonly Func<RegistryPackageMediaItemViewModel, Task> _onSelectAsync;
    private readonly Func<CancellationToken, Task<RemoteImageLoadResult>>? _loadImageAsync;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(RegistryPackageMediaItemViewModel));
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _loadTask;
    private bool _disposed;

    public RegistryPackageMediaItemViewModel(
        RegistryPackageMedia media,
        Func<RegistryPackageMediaItemViewModel, Task> onSelectAsync,
        IUiDispatcher? uiDispatcher = null,
        RemoteImageService? remoteImageService = null,
        Uri? registryOrigin = null)
        : this(
            media,
            onSelectAsync,
            CreateImageLoader(media.Url, remoteImageService, registryOrigin),
            uiDispatcher)
    {
    }

    internal RegistryPackageMediaItemViewModel(
        RegistryPackageMedia media,
        Func<RegistryPackageMediaItemViewModel, Task> onSelectAsync,
        Func<CancellationToken, Task<RemoteImageLoadResult>>? loadImageAsync,
        IUiDispatcher? uiDispatcher)
    {
        Url = media.Url;
        Caption = media.AltText ?? media.FileName;
        FileName = media.FileName;
        Size = media.Size;
        _onSelectAsync = onSelectAsync;
        _loadImageAsync = loadImageAsync;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
    }

    public string Url { get; }

    public string Caption { get; }

    public string FileName { get; }

    public long Size { get; }

    [ObservableProperty]
    private IImage? _image;

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

    public void LoadWhenVisible()
        => _tasks.Observe(EnsureImageLoadedAsync(_disposeCts.Token), "loading visible marketplace media");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _tasks.Dispose();
        DisposeImage(Image);
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
        IImage? image = null;
        await SetImageLoadingAsync(true);
        try
        {
            if (_loadImageAsync is null)
            {
                await ClearImageAsync(cancellationToken);
                return;
            }

            var result = await _loadImageAsync(cancellationToken).ConfigureAwait(false);
            if (result.Error is not null || result.Image is null)
            {
                await ClearImageAsync(cancellationToken);
                return;
            }

            image = result.Image;
            await ApplyLoadedImageAsync(image, cancellationToken);
            image = null;
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
            DisposeImage(image);
            await SetImageLoadingAsync(false);
        }
    }

    private Task ApplyLoadedImageAsync(IImage image, CancellationToken cancellationToken)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                DisposeImage(image);
                return;
            }

            DisposeImage(Image);
            Image = image;
        });
    }

    private Task ClearImageAsync(CancellationToken cancellationToken)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DisposeImage(Image);
            Image = null;
        });
    }

    private Task SetImageLoadingAsync(bool value)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                IsImageLoading = value;
            }
        });
    }

    private static Func<CancellationToken, Task<RemoteImageLoadResult>>? CreateImageLoader(
        string url,
        RemoteImageService? remoteImageService,
        Uri? registryOrigin)
        => remoteImageService is not null
           && registryOrigin is not null
           && RemoteImageService.TryResolveRegistryImageUri(registryOrigin, url, out var imageUri)
            ? token => remoteImageService.LoadRegistryMediaAsync(registryOrigin, imageUri, token)
            : null;

    private static void DisposeImage(IImage? image)
    {
        if (image is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
