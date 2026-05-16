using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageSearchItemViewModel : ViewModelBase, IDisposable
{
    private readonly Func<RegistryPackageSearchItemViewModel, Task> _onSelectAsync;
    private bool _isDisposed;

    public RegistryPackageSearchItemViewModel(
        RegistryPackageSummary package,
        string? installedVersion,
        RegistryPackageUpdate? update,
        Func<RegistryPackageSearchItemViewModel, Task> onSelectAsync,
        bool loadIcon = true)
    {
        PackageId = package.PackageId;
        Name = package.Name;
        Glyph = ToGlyph(package.Name);
        IconUri = TryCreateIconUri(package.IconUrl);
        Summary = package.Summary;
        LatestVersion = package.LatestVersion;
        IsYanked = package.IsYanked;
        InstalledVersion = installedVersion;
        Update = update;
        _onSelectAsync = onSelectAsync;

        if (loadIcon && IconUri is not null)
        {
            _ = LoadIconAsync(IconUri);
        }
    }

    public string PackageId { get; }

    public string Name { get; }

    public string Glyph { get; }

    public Uri? IconUri { get; }

    public bool HasIconImage => IconImage is not null;

    public bool ShowGlyphFallback => IconImage is null;

    public bool HasIconLoadError => !string.IsNullOrWhiteSpace(IconLoadError);

    public bool ShowOperationStatus => HasActiveOperation;

    public string? Summary { get; }

    public string? LatestVersion { get; }

    public bool IsYanked { get; }

    public string LatestVersionText => LatestVersion ?? "No latest";

    public string InstalledVersionText => InstalledVersion is null ? "Not installed" : $"Installed {InstalledVersion}";

    public bool HasUpdate => Update is not null;

    public string ActionText => HasUpdate ? $"Update {Update!.AvailableVersion}" : InstalledVersion is null ? "Install" : "Installed";

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private RegistryPackageUpdate? _update;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private IImage? _iconImage;

    [ObservableProperty]
    private string _iconLoadError = string.Empty;

    [ObservableProperty]
    private bool _hasActiveOperation;

    [ObservableProperty]
    private bool _operationCanCancel;

    [ObservableProperty]
    private bool _operationIsIndeterminate = true;

    [ObservableProperty]
    private double _operationProgressPercent;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnUpdateChanged(RegistryPackageUpdate? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnIconImageChanged(IImage? value)
    {
        OnPropertyChanged(nameof(HasIconImage));
        OnPropertyChanged(nameof(ShowGlyphFallback));
    }

    partial void OnIconLoadErrorChanged(string value)
        => OnPropertyChanged(nameof(HasIconLoadError));

    partial void OnHasActiveOperationChanged(bool value)
        => OnPropertyChanged(nameof(ShowOperationStatus));

    [RelayCommand]
    private async Task SelectAsync() => await _onSelectAsync(this);

    public void Dispose()
    {
        _isDisposed = true;
        if (IconImage is IDisposable disposable)
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

    private static Uri? TryCreateIconUri(string? iconUrl)
    {
        if (!Uri.TryCreate(iconUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static string ToGlyph(string name)
        => string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim()[0].ToString().ToUpperInvariant();
}
