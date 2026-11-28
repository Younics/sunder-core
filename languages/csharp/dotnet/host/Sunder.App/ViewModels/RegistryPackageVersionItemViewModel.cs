using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageVersionItemViewModel : ViewModelBase
{
    private readonly Action<RegistryPackageVersionItemViewModel> _onSelect;

    public RegistryPackageVersionItemViewModel(
        RegistryPackageVersionSummary version,
        Action<RegistryPackageVersionItemViewModel> onSelect,
        string? latestVersion = null)
    {
        Version = version.Version;
        IsYanked = version.IsYanked;
        DeprecatedMessage = version.DeprecatedMessage;
        PublishedAtText = version.PublishedAtUtc.ToLocalTime().ToString("g");
        StatusText = ToStatusText(version, latestVersion);
        _onSelect = onSelect;
    }

    public string Version { get; }

    public bool IsYanked { get; }

    public string? DeprecatedMessage { get; }

    public string PublishedAtText { get; }

    public string StatusText { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _onSelect(this);

    private static string ToStatusText(RegistryPackageVersionSummary version, string? latestVersion)
    {
        if (version.IsYanked) return "Yanked";
        if (!string.IsNullOrWhiteSpace(version.DeprecatedMessage)) return "Deprecated";
        if (version.Version == latestVersion) return "Latest";
        return SemanticVersion.TryParse(version.Version, out var parsed) && parsed.Prerelease is not null ? "Prerelease" : string.Empty;
    }
}
