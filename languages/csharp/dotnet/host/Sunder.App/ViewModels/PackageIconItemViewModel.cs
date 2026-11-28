using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public abstract partial class PackageIconItemViewModel : ViewModelBase, IDisposable
{
    private readonly bool _ownsIconImage;
    private CancellationTokenSource? _iconLoadCts;
    private Task? _iconLoadTask;
    private Action? _startIconLoad;
    private bool _isDisposed;

    protected PackageIconItemViewModel(
        Uri? iconUri,
        IImage? iconImage = null,
        bool ownsIconImage = true,
        bool loadIcon = true,
        IUiDispatcher? uiDispatcher = null,
        PackageIconTransport iconTransport = PackageIconTransport.RuntimeAsset,
        Uri? registryOrigin = null,
        RemoteImageService? remoteImageService = null,
        bool deferIcon = false)
    {
        IconUri = iconUri;
        _ownsIconImage = ownsIconImage;
        IconImage = iconImage;

        if (loadIcon && IconUri is not null && IconImage is null)
        {
            _startIconLoad = () =>
            {
                _iconLoadCts = new CancellationTokenSource();
                _iconLoadTask = PackageIconImageViewModelLoader.LoadAsync(
                    IconUri, iconTransport, registryOrigin, remoteImageService, uiDispatcher ?? AvaloniaUiDispatcher.Instance,
                    () => _isDisposed, (error, image) => { IconLoadError = error; IconImage = image; }, _iconLoadCts.Token);
            };
            if (!deferIcon) EnsureIconLoaded();
        }
    }

    internal void EnsureIconLoaded()
    {
        if (!_isDisposed) Interlocked.Exchange(ref _startIconLoad, null)?.Invoke();
    }

    public Uri? IconUri { get; }

    public bool HasIconImage => IconImage is not null;

    public bool ShowGlyphFallback => IconImage is null;

    public bool HasIconLoadError => !string.IsNullOrWhiteSpace(IconLoadError);

    [ObservableProperty]
    private IImage? _iconImage;

    [ObservableProperty]
    private string _iconLoadError = string.Empty;

    partial void OnIconImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(HasIconImage));
        OnPropertyChanged(nameof(ShowGlyphFallback));
    }

    partial void OnIconLoadErrorChanged(string value)
        => OnPropertyChanged(nameof(HasIconLoadError));

    public virtual void Dispose()
    {
        _isDisposed = true;
        _startIconLoad = null;
        if (_iconLoadCts is not null)
        {
            _iconLoadCts.Cancel();
            if (_iconLoadTask?.IsCompleted == false)
            {
                _ = _iconLoadTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        _iconLoadCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                _iconLoadCts.Dispose();
            }
        }

        if (_ownsIconImage)
        {
            PackageIconImageViewModelLoader.DisposeImage(IconImage);
        }

        IconImage = null;
        GC.SuppressFinalize(this);
    }
}
