using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageVersionItemViewModel : ViewModelBase
{
    private readonly Action<RegistryPackageVersionItemViewModel> _onSelect;

    public RegistryPackageVersionItemViewModel(
        RegistryPackageVersionSummary version,
        Action<RegistryPackageVersionItemViewModel> onSelect)
    {
        Version = version.Version;
        IsYanked = version.IsYanked;
        DeprecatedMessage = version.DeprecatedMessage;
        PublishedAtText = version.PublishedAtUtc.ToLocalTime().ToString("g");
        StatusText = ToStatusText(version);
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

    private static string ToStatusText(RegistryPackageVersionSummary version)
    {
        if (version.IsYanked)
        {
            return "Yanked";
        }

        return string.IsNullOrWhiteSpace(version.DeprecatedMessage) ? "Available" : "Deprecated";
    }
}
