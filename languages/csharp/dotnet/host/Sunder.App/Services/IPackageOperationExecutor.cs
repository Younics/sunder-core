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

    BackgroundProcessSnapshot EnqueueRegistrySourceAdoption(
        string packageId,
        string displayName,
        Uri registryUrl);

    BackgroundProcessSnapshot EnqueueUpdateAll();

    BackgroundProcessSnapshot EnqueueLocalInstall(
        string packagePath,
        string expectedSha256,
        bool deleteAfterUse);

    BackgroundProcessSnapshot EnqueueLocalInstall(
        string packagePath,
        string expectedSha256,
        bool deleteAfterUse,
        PackageCapabilityConsent? capabilityConsent)
        => capabilityConsent is null
            ? EnqueueLocalInstall(packagePath, expectedSha256, deleteAfterUse)
            : throw new NotSupportedException("This package operation test executor does not support sensitive capability consent.");

    BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName);

    BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName);

    BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName, bool allowCascade = false);
}
