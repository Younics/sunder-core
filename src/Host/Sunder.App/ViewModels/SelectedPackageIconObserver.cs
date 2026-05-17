using System.ComponentModel;

namespace Sunder.App.ViewModels;

internal sealed class SelectedPackageIconObserver(Action<SelectedPackageIconState> applyIconState) : IDisposable
{
    private PackageCatalogItemViewModel? _installedPackage;
    private RegistryPackageSearchItemViewModel? _marketplacePackage;

    public void ObserveInstalled(PackageCatalogItemViewModel? item)
    {
        if (_installedPackage is not null)
        {
            _installedPackage.PropertyChanged -= InstalledPackage_OnPropertyChanged;
        }

        _installedPackage = item;
        if (_installedPackage is not null)
        {
            _installedPackage.PropertyChanged += InstalledPackage_OnPropertyChanged;
        }

        RefreshInstalledIcon();
    }

    public void ObserveMarketplace(RegistryPackageSearchItemViewModel? item)
    {
        if (_marketplacePackage is not null)
        {
            _marketplacePackage.PropertyChanged -= MarketplacePackage_OnPropertyChanged;
        }

        _marketplacePackage = item;
        if (_marketplacePackage is not null)
        {
            _marketplacePackage.PropertyChanged += MarketplacePackage_OnPropertyChanged;
        }

        RefreshMarketplaceIcon();
    }

    public void Dispose()
    {
        ObserveInstalled(null);
        ObserveMarketplace(null);
    }

    private void InstalledPackage_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageCatalogItemViewModel.IconImage) or nameof(PackageCatalogItemViewModel.IconLoadError))
        {
            RefreshInstalledIcon();
        }
    }

    private void MarketplacePackage_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RegistryPackageSearchItemViewModel.IconImage) or nameof(RegistryPackageSearchItemViewModel.IconLoadError))
        {
            RefreshMarketplaceIcon();
        }
    }

    private void RefreshInstalledIcon()
    {
        applyIconState(_installedPackage is null
            ? SelectedPackageIconState.Empty
            : new SelectedPackageIconState(
                _installedPackage.Glyph,
                _installedPackage.IconImage,
                _installedPackage.IconLoadError ?? string.Empty));
    }

    private void RefreshMarketplaceIcon()
    {
        applyIconState(_marketplacePackage is null
            ? SelectedPackageIconState.Empty
            : new SelectedPackageIconState(
                _marketplacePackage.Glyph,
                _marketplacePackage.IconImage,
                _marketplacePackage.IconLoadError ?? string.Empty));
    }
}
