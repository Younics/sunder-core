using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal interface IPackageOperationExecutor
{
    event EventHandler<PackageOperationChangedEventArgs>? OperationChanged;
    BackgroundTaskSnapshot? GetActiveOperationForPackage(string packageId);
    BackgroundTaskSnapshot? GetActivePackageStoreOperation();
    bool CancelActiveOperationForPackage(string packageId);
    Task<BackgroundTaskSnapshot?> EnqueueMarketplaceInstallAsync(string packageId, string displayName, Uri registryUrl,
        string? version = null, string? tag = "latest", CancellationToken cancellationToken = default);
    Task<BackgroundTaskSnapshot?> EnqueueMarketplaceUpdateAsync(string packageId, string displayName, string version, CancellationToken cancellationToken = default);
    Task<BackgroundTaskSnapshot?> EnqueueRegistrySourceAdoptionAsync(string packageId, string displayName, Uri registryUrl, CancellationToken cancellationToken = default);
    Task<BackgroundTaskSnapshot?> EnqueueUpdateAllAsync(CancellationToken cancellationToken = default);
    Task<BackgroundTaskSnapshot?> EnqueueLocalInstallAsync(PackageArchiveSelection selection, CancellationToken cancellationToken = default);
    BackgroundTaskSnapshot EnqueueEnable(string packageId, string displayName);
    BackgroundTaskSnapshot EnqueueDisable(string packageId, string displayName);
    BackgroundTaskSnapshot EnqueueUninstall(string packageId, string displayName, bool allowCascade = false);
}
