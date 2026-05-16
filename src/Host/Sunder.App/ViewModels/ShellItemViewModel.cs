using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public partial class ShellItemViewModel : ViewModelBase, IDisposable
{
    private readonly Action<ShellItemViewModel> _onSelect;
    private readonly bool _ownsIconImage;
    private bool _isDisposed;

    public ShellItemViewModel(
        string id,
        string glyph,
        Uri? iconUri,
        string title,
        string packageDisplayName,
        string toolTipText,
        Models.RailPlacement placement,
        Action<ShellItemViewModel> onSelect,
        bool isDragPreview = false,
        IImage? iconImage = null,
        bool ownsIconImage = true)
    {
        Id = id;
        Glyph = glyph;
        IconUri = iconUri;
        Title = title;
        PackageDisplayName = packageDisplayName;
        ToolTipText = toolTipText;
        Placement = placement;
        _onSelect = onSelect;
        IsDragPreview = isDragPreview;
        _ownsIconImage = ownsIconImage;
        IconImage = iconImage;

        if (IconUri is not null && IconImage is null)
        {
            _ = LoadIconAsync(IconUri);
        }
    }

    public string Id { get; }

    public string Glyph { get; }

    public Uri? IconUri { get; }

    public bool HasIconImage => IconImage is not null;

    public bool ShowGlyphFallback => IconImage is null;

    public bool HasIconLoadError => !string.IsNullOrWhiteSpace(IconLoadError);

    public string Title { get; }

    public string PackageDisplayName { get; }

    public string ToolTipText { get; }

    public Models.RailPlacement Placement { get; }

    public bool IsDragPreview { get; }

    public bool IsHorizontalBar => Placement == Models.RailPlacement.Middle;

    public bool IsVerticalBar => !IsHorizontalBar;

    public string MenuText => $"{PackageDisplayName} · {Title}";

    public void Activate() => _onSelect(this);

    [ObservableProperty]
    private bool _isSelected;

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

    [RelayCommand]
    private void Select() => _onSelect(this);

    public void Dispose()
    {
        _isDisposed = true;
        if (_ownsIconImage && IconImage is IDisposable disposable)
        {
            disposable.Dispose();
        }

        IconImage = null;
    }

    private async Task LoadIconAsync(Uri iconUri)
    {
        var result = await PackageIconImageLoader.LoadAsync(iconUri);
        await UiThread.InvokeAsync(() =>
        {
            if (_isDisposed)
            {
                if (result.Image is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                return;
            }

            IconLoadError = result.Error ?? string.Empty;
            IconImage = result.Image;
        });
    }
}
