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

    Uri CreatePackageAssetUri(string packageId, string assetPath);

    Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> EnableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> DisableInstalledPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<PackageOperationResult> UninstallPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<DevPackageLoadResult> LoadDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default);

    Task<DevPackageStageResult> StageDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support dev package staging.");

    Task<DevPackageLoadResult> CommitDevPackageStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support dev package staging.");

    Task DiscardDevPackageStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Runtime client does not support dev package staging.");

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
