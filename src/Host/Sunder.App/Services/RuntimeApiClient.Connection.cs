using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
        => GetSystemStatusCoreAsync(cancellationToken);

    private async Task<SystemStatusResponse?> GetSystemStatusCoreAsync(CancellationToken cancellationToken)
        => await _management.GetSystemStatusAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
        => _management.IsRuntimeHealthyAsync(cancellationToken);

    public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
        => _management.GetActivePackagesAsync(cancellationToken);

    public Task<RuntimePackageSnapshot> GetRuntimePackageSnapshotAsync(CancellationToken cancellationToken = default)
        => _management.GetPackageSnapshotAsync(cancellationToken);

    public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
        => _management.GetSessionPackagesAsync(cancellationToken);

    public Task<RuntimeHandshakeResponse> GetRuntimeHandshakeAsync(CancellationToken cancellationToken = default)
        => _management.GetRuntimeHandshakeAsync(cancellationToken);

    public Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerMutationRequest request,
        CancellationToken cancellationToken = default)
        => _management.ReplaceDevPackageOwnerAsync(ownerId, request, cancellationToken);

    public Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerHeartbeatRequest request,
        CancellationToken cancellationToken = default)
        => _management.HeartbeatDevPackageOwnerAsync(ownerId, request, cancellationToken);

    public Task ReleaseDevPackageOwnerAsync(
        string ownerId,
        DevPackageOwnerReleaseRequest request,
        CancellationToken cancellationToken = default)
        => _management.ReleaseDevPackageOwnerAsync(ownerId, request, cancellationToken);

    public Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(long afterSequenceId = 0, CancellationToken cancellationToken = default)
        => _management.GetRuntimeEventSnapshotAsync(afterSequenceId, cancellationToken);

    public IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(long afterSequenceId, CancellationToken cancellationToken = default)
        => _management.StreamRuntimeEventsAsync(afterSequenceId, cancellationToken);

    public Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(long afterSequenceId = 0, int limit = 500, CancellationToken cancellationToken = default)
        => _management.GetPackageLogSnapshotAsync(afterSequenceId, limit, cancellationToken);

    public IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(long afterSequenceId, CancellationToken cancellationToken = default)
        => _management.StreamPackageLogsAsync(afterSequenceId, cancellationToken);

    public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
        string appRid,
        CancellationToken cancellationToken = default)
        => _management.GetActivePackageUiSnapshotsAsync(appRid, cancellationToken);

    public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetStagedPackageUiSnapshotsAsync(
        string stageId,
        string appRid,
        CancellationToken cancellationToken = default)
        => _management.GetStagedPackageUiSnapshotsAsync(stageId, appRid, cancellationToken);

    public Task DownloadPackageUiSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken = default)
        => _management.DownloadPackageUiSnapshotAsync(
            snapshot,
            destination,
            AppPackageSourcePreparer.MaxSnapshotBytes,
            cancellationToken);

    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
        => _management.GetInstalledPackagesAsync(cancellationToken);

    public Uri CreatePackageAssetUri(string packageId, string assetPath)
        => _management.CreatePackageAssetUri(packageId, assetPath);

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
        => _management.ShutdownAsync(cancellationToken);
}
