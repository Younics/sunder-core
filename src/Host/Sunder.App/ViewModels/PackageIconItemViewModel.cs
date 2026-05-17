using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public abstract partial class PackageIconItemViewModel : ViewModelBase, IDisposable
{
    private readonly bool _ownsIconImage;
    private readonly CancellationTokenSource? _iconLoadCts;
    private readonly Task? _iconLoadTask;
    private bool _isDisposed;

    protected PackageIconItemViewModel(
        Uri? iconUri,
        IImage? iconImage = null,
        bool ownsIconImage = true,
        bool loadIcon = true)
    {
        IconUri = iconUri;
        _ownsIconImage = ownsIconImage;
        IconImage = iconImage;

        if (loadIcon && IconUri is not null && IconImage is null)
        {
            _iconLoadCts = new CancellationTokenSource();
            _iconLoadTask = PackageIconImageViewModelLoader.LoadAsync(
                IconUri,
                () => _isDisposed,
                (error, image) =>
                {
                    IconLoadError = error;
                    IconImage = image;
                },
                _iconLoadCts.Token);
        }
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
