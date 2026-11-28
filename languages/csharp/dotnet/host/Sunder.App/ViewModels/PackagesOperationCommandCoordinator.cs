using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal sealed class PackagesOperationCommandCoordinator(
    IPackageOperationExecutor operationExecutor,
    IPackageArchivePicker packageArchivePicker,
    PackageRegistryClientProvider registryClientProvider,
    PackageOperationPresentationViewModel operations,
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
            operations.StatusText = "Preparing installation review…";
            if (await operationExecutor.EnqueueLocalInstallAsync(selection) is null)
            {
                operations.StatusText = "Installation cancelled before queueing.";
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

    public async Task UninstallPackageAsync(string packageId, string displayName)
    {
        operations.StatusText = $"Checking what will be removed with {displayName}…";
        try
        {
            if (await operationExecutor.EnqueueUninstallAsync(packageId, displayName) is null)
            {
                operations.StatusText = "Uninstall cancelled.";
                return;
            }
        }
        catch (Exception error)
        {
            operations.ReportError($"Could not queue uninstall for {displayName}: {error.Message}");
            return;
        }
        MarkQueued($"Queued uninstall for {displayName}.");
    }

    public Task InstallMarketplacePackageAsync(string packageId, string displayName, string selectedVersion)
        => TryResolveRegistryUrl(out var registryUrl)
            ? InstallMarketplacePackageAsync(packageId, displayName, selectedVersion, registryUrl, CancellationToken.None)
            : Task.CompletedTask;

    public async Task InstallMarketplacePackageAsync(string packageId, string displayName, string selectedVersion,
        Uri registryUrl, CancellationToken cancellationToken)
    {
        operations.StatusText = "Preparing installation review…";
        if (await operationExecutor.EnqueueMarketplaceInstallAsync(packageId, displayName, registryUrl, selectedVersion,
                tag: null, cancellationToken: cancellationToken) is not null)
            MarkQueued($"Queued install for {packageId} {selectedVersion}.");
        else operations.StatusText = "Installation cancelled before queueing.";
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
            else operations.StatusText = "Installation cancelled before queueing.";
        }

    }

    public async Task UpdateAllPackagesAsync()
    {
        if (await operationExecutor.EnqueueUpdateAllAsync() is not null) MarkQueued("Queued updates for installed packages.");
        else operations.StatusText = "Installation cancelled before queueing.";
    }

    private async Task EnqueueMarketplaceUpdateAsync(RegistryPackageUpdate update, string displayName)
    {
        if (await operationExecutor.EnqueueMarketplaceUpdateAsync(update.PackageId, displayName, update.AvailableVersion) is not null)
            MarkQueued($"Queued update for {update.PackageId}.");
        else operations.StatusText = "Installation cancelled before queueing.";
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
        operations.ReportError(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        return false;
    }

    private void MarkQueued(string statusText)
    {
        markInstalledCatalogDirty();
        refreshPackageOperationState();
        operations.StatusText = statusText;
    }
}
