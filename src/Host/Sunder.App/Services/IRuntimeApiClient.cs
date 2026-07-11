using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.Services;

public interface IRuntimeApiClient : IDisposable
{
    Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default);

    Task<DevPackageWatchStatus> SetDevPackageWatchIntentAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support dev-package watch intent.");

    Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(
        long afterSequenceId = 0,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Runtime event snapshots.");

    IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(
        long afterSequenceId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support Runtime event streaming.");

    Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(
        long afterSequenceId = 0,
        int limit = 500,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-log snapshots.");

    IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(
        long afterSequenceId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package-log streaming.");

    Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

    Task DownloadPackageUiSnapshotAsync(
        PackageUiSnapshotDescriptor snapshot,
        Stream destination,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support package UI snapshot downloads.");

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

    Task<ContentUploadDescriptor> UploadPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContentUploadDescriptor(packagePath, string.Empty, 0, Path.GetFileName(packagePath), "application/vnd.sunder.package"));

    Task<ContentUploadDescriptor> UploadStackAsync(
        string stackPath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContentUploadDescriptor(stackPath, string.Empty, 0, Path.GetFileName(stackPath), "application/vnd.sunder.stack"));

    Task<ContentUploadDescriptor> UploadStackMediaAsync(
        string mediaPath,
        string contentType,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContentUploadDescriptor(mediaPath, string.Empty, 0, Path.GetFileName(mediaPath), contentType));

    Task DownloadContentAsync(
        ContentDownloadDescriptor download,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support content downloads.");

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

    Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(
        RuntimeRegistryAuthStartRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(
        string registryOrigin,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(
        string registryOrigin,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(
        RuntimeRegistryPackageRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(
        RuntimeRegistryUpdateRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(
        RuntimeRegistryStarRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RegistryStackStarResponse> SetRegistryStackStarAsync(
        RuntimeRegistryStarRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RegistryPublishStackResponse> PublishRegistryStackAsync(
        RuntimeRegistryPublishRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(
        RuntimeRegistryDeleteStackRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
