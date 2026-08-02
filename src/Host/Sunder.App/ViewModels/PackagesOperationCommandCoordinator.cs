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
            operationExecutor.EnqueueLocalInstall(
                selection.PackagePath,
                selection.Sha256,
                selection.DeleteAfterUse);
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

    public Task InstallMarketplacePackageAsync(string packageId, string displayName, string selectedVersion)
    {
        if (TryResolveRegistryUrl(out var registryUrl))
        {
            operationExecutor.EnqueueMarketplaceInstall(packageId, displayName, registryUrl, selectedVersion, tag: null);
            MarkQueued($"Queued install for {packageId} {selectedVersion}.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateInstalledPackageAsync(RegistryPackageUpdate update, string displayName)
        => EnqueueMarketplaceUpdateAsync(update, displayName);

    public Task UpdateMarketplacePackageAsync(
        RegistryPackageUpdate update,
        string displayName)
        => EnqueueMarketplaceUpdateAsync(update, displayName);

    public Task UpdateAllPackagesAsync()
    {
        operationExecutor.EnqueueUpdateAll();
        MarkQueued("Queued updates for installed packages.");

        return Task.CompletedTask;
    }

    private Task EnqueueMarketplaceUpdateAsync(RegistryPackageUpdate update, string displayName)
    {
        operationExecutor.EnqueueMarketplaceUpdate(update.PackageId, displayName, update.AvailableVersion);
        MarkQueued($"Queued update for {update.PackageId}.");

        return Task.CompletedTask;
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
