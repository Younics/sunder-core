using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageSearchItemViewModel : PackageIconItemViewModel, IPackageOperationStateViewModel
{
    private readonly Func<RegistryPackageSearchItemViewModel, Task> _onSelectAsync;

    public RegistryPackageSearchItemViewModel(
        RegistryPackageSummary package,
        string? installedVersion,
        RegistryPackageUpdate? update,
        Func<RegistryPackageSearchItemViewModel, Task> onSelectAsync,
        bool loadIcon = true)
        : base(TryCreateIconUri(package.IconUrl), loadIcon: loadIcon)
    {
        PackageId = package.PackageId;
        Name = package.Name;
        Glyph = ToGlyph(package.Name);
        Summary = package.Summary;
        LatestVersion = package.LatestVersion;
        IsYanked = package.IsYanked;
        InstalledVersion = installedVersion;
        Update = update;
        _onSelectAsync = onSelectAsync;
    }

    public string PackageId { get; }

    public string Name { get; }

    public string Glyph { get; }

    public bool ShowOperationStatus => HasActiveOperation;

    public string? Summary { get; }

    public string? LatestVersion { get; }

    public bool IsYanked { get; }

    public string LatestVersionText => LatestVersion ?? "No latest";

    public string InstalledVersionText => InstalledVersion is null ? "Not installed" : $"Installed {InstalledVersion}";

    public bool IsInstalled => InstalledVersion is not null;

    public bool ShowRowBadges => IsInstalled || HasUpdate;

    public bool HasUpdate => Update is not null;

    public string ActionText => HasUpdate ? $"Update {Update!.AvailableVersion}" : InstalledVersion is null ? "Install" : "Installed";

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private RegistryPackageUpdate? _update;

    [ObservableProperty]
    private bool _isSelected;

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
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ShowRowBadges));
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnUpdateChanged(RegistryPackageUpdate? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowRowBadges));
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnHasActiveOperationChanged(bool value)
        => OnPropertyChanged(nameof(ShowOperationStatus));

    [RelayCommand]
    private async Task SelectAsync() => await _onSelectAsync(this);

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
