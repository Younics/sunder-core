using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal interface IPackageOperationExecutor
{
    event EventHandler<PackageOperationChangedEventArgs>? OperationChanged;
    BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId);
    BackgroundProcessSnapshot? GetActivePackageStoreOperation();
    bool CancelActiveOperationForPackage(string packageId);
    Task<BackgroundProcessSnapshot?> EnqueueMarketplaceInstallAsync(string packageId, string displayName, Uri registryUrl,
        string? version = null, string? tag = "latest", CancellationToken cancellationToken = default);
    Task<BackgroundProcessSnapshot?> EnqueueMarketplaceUpdateAsync(string packageId, string displayName, string version, CancellationToken cancellationToken = default);
    Task<BackgroundProcessSnapshot?> EnqueueRegistrySourceAdoptionAsync(string packageId, string displayName, Uri registryUrl, CancellationToken cancellationToken = default);
    Task<BackgroundProcessSnapshot?> EnqueueUpdateAllAsync(CancellationToken cancellationToken = default);
    Task<BackgroundProcessSnapshot?> EnqueueLocalInstallAsync(PackageArchiveSelection selection, CancellationToken cancellationToken = default);
    BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName);
    BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName);
    BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName, bool allowCascade = false);
}
