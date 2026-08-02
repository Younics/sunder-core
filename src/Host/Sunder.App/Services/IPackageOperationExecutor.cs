using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal interface IPackageOperationExecutor
{
    event EventHandler<PackageOperationChangedEventArgs>? OperationChanged;

    BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId);

    BackgroundProcessSnapshot? GetActivePackageStoreOperation();

    bool CancelActiveOperationForPackage(string packageId);

    BackgroundProcessSnapshot EnqueueMarketplaceInstall(
        string packageId,
        string displayName,
        Uri registryUrl,
        string? version = null,
        string? tag = "latest");

    BackgroundProcessSnapshot EnqueueMarketplaceUpdate(
        string packageId,
        string displayName,
        string version);

    BackgroundProcessSnapshot EnqueueUpdateAll();

    BackgroundProcessSnapshot EnqueueLocalInstall(
        string packagePath,
        string expectedSha256,
        bool deleteAfterUse);

    BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName);

    BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName);

    BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName, bool allowCascade = false);
}
