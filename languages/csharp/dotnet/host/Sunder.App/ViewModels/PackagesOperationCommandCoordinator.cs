using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal sealed class PackagesOperationCommandCoordinator(
    IPackageOperationExecutor operationExecutor,
    IPackageArchivePicker packageArchivePicker,
    PackageRegistryClientProvider registryClientProvider,
    Action<string> setStatusText,
    Action refreshPackageOperationState,
    Action markInstalledCatalogDirty)
{
    public bool HasActivePackageStoreOperation
        => operationExecutor.GetActivePackageStoreOperation()?.IsActive == true;

    public async Task<bool> InstallFromDiskAsync()
    {
        var selection = await packageArchivePicker.PickPackageAsync();
        if (selection is null)
        {
            return false;
        }

        try
        {
            setStatusText("Preparing installation review…");
            if (await operationExecutor.EnqueueLocalInstallAsync(selection) is null)
            {
                setStatusText("Installation cancelled before queueing.");
                return false;
            }
        }
        catch
        {
            if (selection.DeleteAfterUse)
            {
                PackageArchivePicker.DeleteReviewSnapshot(selection.PackagePath);
            }
            throw;
        }
        MarkQueued($"Queued install for {Path.GetFileName(selection.PackagePath)}.");
        return true;
    }

    public Task EnableInstalledPackageAsync(string packageId, string displayName)
    {
        if (!HasActivePackageStoreOperation)
        {
            operationExecutor.EnqueueEnable(packageId, displayName);
            MarkQueued($"Queued enable for {packageId}.");
        }

        return Task.CompletedTask;
    }

    public Task DisableInstalledPackageAsync(string packageId, string displayName)
    {
        if (!HasActivePackageStoreOperation)
        {
            operationExecutor.EnqueueDisable(packageId, displayName);
            MarkQueued($"Queued disable for {packageId}.");
        }

        return Task.CompletedTask;
    }

    public Task UninstallPackageAsync(string packageId, string displayName)
    {
        operationExecutor.EnqueueUninstall(packageId, displayName);
        MarkQueued($"Queued uninstall for {packageId}.");
        return Task.CompletedTask;
    }

    public async Task InstallMarketplacePackageAsync(string packageId, string displayName, string selectedVersion)
    {
        if (TryResolveRegistryUrl(out var registryUrl))
        {
            setStatusText("Preparing installation review…");
            if (await operationExecutor.EnqueueMarketplaceInstallAsync(packageId, displayName, registryUrl, selectedVersion, tag: null) is not null)
                MarkQueued($"Queued install for {packageId} {selectedVersion}.");
            else setStatusText("Installation cancelled before queueing.");
        }

    }

    public Task UpdateInstalledPackageAsync(RegistryPackageUpdate update, string displayName)
        => EnqueueMarketplaceUpdateAsync(update, displayName);

    public Task UpdateMarketplacePackageAsync(
        RegistryPackageUpdate update,
        string displayName)
        => EnqueueMarketplaceUpdateAsync(update, displayName);

    public async Task AdoptRegistrySourceAsync(string packageId, string displayName)
    {
        if (TryResolveRegistryUrl(out var registryUrl))
        {
            if (await operationExecutor.EnqueueRegistrySourceAdoptionAsync(packageId, displayName, registryUrl) is not null)
                MarkQueued($"Queued Registry source adoption for {packageId}.");
            else setStatusText("Installation cancelled before queueing.");
        }

    }

    public async Task UpdateAllPackagesAsync()
    {
        if (await operationExecutor.EnqueueUpdateAllAsync() is not null) MarkQueued("Queued updates for installed packages.");
        else setStatusText("Installation cancelled before queueing.");
    }

    private async Task EnqueueMarketplaceUpdateAsync(RegistryPackageUpdate update, string displayName)
    {
        if (await operationExecutor.EnqueueMarketplaceUpdateAsync(update.PackageId, displayName, update.AvailableVersion) is not null)
            MarkQueued($"Queued update for {update.PackageId}.");
        else setStatusText("Installation cancelled before queueing.");
    }

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (registryClientProvider.TryResolve(out var resolvedRegistryUrl, out var errorMessage)
            && resolvedRegistryUrl is not null)
        {
            registryUrl = resolvedRegistryUrl;
            return true;
        }

        registryUrl = null!;
        setStatusText(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        return false;
    }

    private void MarkQueued(string statusText)
    {
        markInstalledCatalogDirty();
        refreshPackageOperationState();
        setStatusText(statusText);
    }
}
