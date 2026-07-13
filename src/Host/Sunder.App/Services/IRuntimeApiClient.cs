using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public interface IRuntimeClient : IDisposable;

public interface IRuntimeConnectionClient : IRuntimeClient
{
    Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeEventClient : IRuntimeClient
{
    Task<DevPackageWatchStatus> SetDevPackageWatchIntentAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(long afterSequenceId = 0, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(long afterSequenceId, CancellationToken cancellationToken = default);
}

public interface IRuntimeLogClient : IRuntimeClient
{
    Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(long afterSequenceId = 0, int limit = 500, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(long afterSequenceId, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageSessionClient : IRuntimeClient
{
    Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default);
    Task<PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default);
    Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default);
    Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default);
    Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(PackageLifecycleStageRequest request, CancellationToken cancellationToken = default);
    Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default);
    Task DiscardPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default);
    Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageUiClient : IRuntimeClient
{
    Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(CancellationToken cancellationToken = default);
    Task DownloadPackageUiSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken = default);
    Uri CreatePackageAssetUri(string packageId, string assetPath);
}

public interface IRuntimeContentTransferClient : IRuntimeClient
{
    Task<ContentUploadDescriptor> UploadPackageAsync(string packagePath, CancellationToken cancellationToken = default);
    Task<ContentUploadDescriptor> UploadStackAsync(string stackPath, CancellationToken cancellationToken = default);
    Task<ContentUploadDescriptor> UploadStackMediaAsync(string mediaPath, string contentType, CancellationToken cancellationToken = default);
    Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageStoreClient : IRuntimeClient
{
    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default);
    Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> UpgradePackageFromPathAsync(string packageId, string packagePath, bool allowDowngrade = false, bool reinstall = false, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default);
    Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default);
    Task DiscardPackageStoreStageAsync(string stageId, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageSettingsClient : IRuntimeClient
{
    Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default);
    Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default);
    Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);
}

public interface IRuntimePackageAuthClient : IRuntimeClient
{
    Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default);
    Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default);
    Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default);
}

public interface IRuntimeStackClient : IRuntimeClient
{
    Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default);
    Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimeRegistryAuthClient : IRuntimeClient
{
    Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string registryOrigin, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string registryOrigin, CancellationToken cancellationToken = default);
}

public interface IRuntimeRegistryPackageClient : IRuntimeClient
{
    Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default);
    Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimeRegistryStackClient : IRuntimeClient
{
    Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default);
    Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken = default);
    Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimeShellClient :
    IRuntimeConnectionClient,
    IRuntimePackageSessionClient,
    IRuntimePackageUiClient;

public interface IRuntimePackageChangeClient :
    IRuntimeContentTransferClient,
    IRuntimePackageStoreClient,
    IRuntimeRegistryPackageClient;

public interface IRuntimePackageUpdateClient : IRuntimeClient
{
    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default);
    Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default);
}

public interface IRuntimePackagesClient :
    IRuntimePackageSessionClient,
    IRuntimePackageUiClient,
    IRuntimePackageChangeClient;

public interface IRuntimeStacksClient :
    IRuntimeStackClient,
    IRuntimeContentTransferClient,
    IRuntimePackageSessionClient,
    IRuntimePackageUiClient,
    IRuntimePackageStoreClient,
    IRuntimeRegistryPackageClient,
    IRuntimeRegistryStackClient;

public interface IRuntimeApiClient :
    IRuntimeConnectionClient,
    IRuntimeEventClient,
    IRuntimeLogClient,
    IRuntimePackageSessionClient,
    IRuntimePackageUiClient,
    IRuntimeContentTransferClient,
    IRuntimePackageStoreClient,
    IRuntimePackageSettingsClient,
    IRuntimePackageAuthClient,
    IRuntimeStackClient,
    IRuntimeRegistryAuthClient,
    IRuntimeRegistryPackageClient,
    IRuntimeRegistryStackClient,
    IRuntimeShellClient,
    IRuntimePackageChangeClient,
    IRuntimePackageUpdateClient,
    IRuntimePackagesClient,
    IRuntimeStacksClient;
