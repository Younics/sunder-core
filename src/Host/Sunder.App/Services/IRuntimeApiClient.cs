using Sunder.Protocol;

namespace Sunder.App.Services;

public interface IRuntimeApiClient : IDisposable
{
    Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default);

    Task<PackageSessionStatus?> GetPackageSessionStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-session status.");

    Task<PackageSessionOperationResult> LoadPackageSessionAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-session loading.");

    Task<PackageSessionOperationResult> UnloadPackageSessionAsync(
        string packageId,
        PackageSourceKind sourceKind,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-session unloading.");

    Uri CreatePackageAssetUri(string packageId, string assetPath);

    Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> InstallPackagesFromPathsAsync(
        PackageInstallBatchFromPathRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support batch package installation.");

    Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        bool applyRuntimeSession,
        CancellationToken cancellationToken = default)
        => InstallPackageFromPathAsync(packagePath, cancellationToken);

    Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade,
        bool reinstall,
        bool applyRuntimeSession,
        CancellationToken cancellationToken = default)
        => UpgradePackageFromPathAsync(packageId, packagePath, allowDowngrade, reinstall, cancellationToken);

    Task<PackageOperationResult> EnableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> DisableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> UninstallPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-store staging.");

    Task<PackageOperationResult> CommitPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-store staging.");

    Task DiscardPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-store staging.");

    Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
        PackageLifecycleLoadRequest request,
        CancellationToken cancellationToken = default);

    Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Stack export discovery.");

    Task<RuntimeStackExportResponse> ExportStackAsync(
        RuntimeStackExportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Stack export.");

    Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Stack import preview.");

    Task<RuntimeStackImportResponse> ImportStackAsync(
        RuntimeStackImportRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Stack import.");

    Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support installed package session reloading.");

    Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(
        PackageLifecycleStageRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package lifecycle staging.");

    Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package lifecycle staging.");

    Task DiscardPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package lifecycle staging.");

    Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(
        CancellationToken cancellationToken = default);

    Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task SavePackageConfigurationValuesAsync(
        string packageId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default);

    Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
        string packageId,
        string authSessionId,
        CancellationToken cancellationToken = default);

    Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task ReportPackageFaultAsync(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
