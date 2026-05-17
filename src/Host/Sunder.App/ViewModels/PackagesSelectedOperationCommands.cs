using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal sealed class PackagesSelectedOperationCommands(
    PackagesOperationCommandCoordinator operationCommands,
    PackageOperationStatePresenter operationState,
    Func<PackageWindowMode> getMode,
    Action<PackageWindowMode> setMode,
    Func<PackageCatalogItemViewModel?> getSelectedInstalledPackage,
    Func<RegistryPackageSearchItemViewModel?> getSelectedMarketplacePackage,
    Func<RegistryPackageVersionItemViewModel?> getSelectedMarketplaceVersion,
    Func<int> getAvailableUpdateCount,
    Func<RegistryPackageUpdate?> getSelectedInstalledPackageUpdate,
    Func<string, RegistryPackageUpdate?> getPackageUpdate,
    Action refreshPackageOperationState,
    Action<string> setStatusText)
{
    public async Task InstallPackageAsync()
    {
        if (await operationCommands.InstallFromDiskAsync())
        {
            setMode(PackageWindowMode.Installed);
        }
    }

    public async Task EnableSelectedPackageAsync()
    {
        var selectedPackage = getSelectedInstalledPackage();
        var packageId = selectedPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        await operationCommands.EnableInstalledPackageAsync(packageId, selectedPackage?.DisplayName ?? packageId);
    }

    public async Task DisableSelectedPackageAsync()
    {
        var selectedPackage = getSelectedInstalledPackage();
        var packageId = selectedPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        await operationCommands.DisableInstalledPackageAsync(packageId, selectedPackage?.DisplayName ?? packageId);
    }

    public async Task UninstallSelectedPackageAsync()
    {
        var selectedPackage = getSelectedInstalledPackage();
        var packageId = selectedPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        await operationCommands.UninstallPackageAsync(packageId, selectedPackage?.DisplayName ?? packageId);
    }

    public async Task InstallSelectedMarketplacePackageAsync()
    {
        var selectedPackage = getSelectedMarketplacePackage();
        var packageId = selectedPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var selectedVersion = getSelectedMarketplaceVersion();
        if (string.IsNullOrWhiteSpace(selectedVersion?.Version) || selectedVersion.IsYanked)
        {
            return;
        }

        await operationCommands.InstallMarketplacePackageAsync(packageId, selectedPackage?.Name ?? packageId, selectedVersion.Version);
    }

    public async Task UpdateSelectedInstalledPackageAsync()
    {
        var update = getSelectedInstalledPackageUpdate();
        if (update is null)
        {
            return;
        }

        await operationCommands.UpdateInstalledPackageAsync(update, getSelectedInstalledPackage()?.DisplayName ?? update.PackageId);
    }

    public async Task UpdateSelectedMarketplacePackageAsync()
    {
        var selectedPackage = getSelectedMarketplacePackage();
        var packageId = selectedPackage?.PackageId;
        var update = string.IsNullOrWhiteSpace(packageId) ? null : getPackageUpdate(packageId);
        if (update is null)
        {
            return;
        }

        await operationCommands.UpdateMarketplacePackageAsync(
            update,
            selectedPackage?.Name ?? update.PackageId,
            getSelectedInstalledPackage()?.PackageId);
    }

    public async Task UninstallSelectedMarketplacePackageAsync()
    {
        var selectedPackage = getSelectedMarketplacePackage();
        var packageId = selectedPackage?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        await operationCommands.UninstallPackageAsync(packageId, selectedPackage?.Name ?? packageId);
    }

    public async Task UpdateAllPackagesAsync()
    {
        if (getAvailableUpdateCount() == 0)
        {
            return;
        }

        await operationCommands.UpdateAllPackagesAsync(getSelectedInstalledPackage()?.PackageId);
    }

    public void CancelSelectedPackageOperation()
    {
        var packageId = getMode() == PackageWindowMode.Marketplace
            ? getSelectedMarketplacePackage()?.PackageId
            : getSelectedInstalledPackage()?.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        if (operationState.CancelActiveOperationForPackage(packageId))
        {
            refreshPackageOperationState();
            setStatusText($"Cancelling package operation for {packageId}...");
        }
    }
}
