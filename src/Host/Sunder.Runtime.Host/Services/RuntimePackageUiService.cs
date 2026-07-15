using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageUiService(
    RuntimeSessionOwner sessions,
    PackageUiSnapshotStore snapshots,
    InstalledPackageStore installedPackages)
{
    public IReadOnlyList<PackageUiSnapshotDescriptor> GetActiveSnapshots()
        => sessions.GetSnapshot().PackageUiSnapshots;

    public IReadOnlyList<PackageUiSnapshotDescriptor> CreateSnapshots(
        IReadOnlyList<RuntimePackageSource> sources,
        long generation,
        string? stageId = null)
        => snapshots.CreateSnapshots(sources, generation, stageId);

    public PackageUiSnapshotLease? AcquireCurrent(string snapshotId)
    {
        var snapshot = sessions.GetSnapshot();
        return snapshot.PackageUiSnapshots.Any(item => string.Equals(item.SnapshotId, snapshotId, StringComparison.Ordinal))
            ? snapshots.Acquire(snapshotId, snapshot.SessionGeneration, stageId: null)
            : null;
    }

    public PackageUiSnapshotLease? AcquireStage(string stageId, string snapshotId)
        => sessions.TryGetStageGeneration(stageId, out var generation)
            ? snapshots.Acquire(snapshotId, generation, stageId)
            : null;

    public IReadOnlyList<PackageUiSnapshotDescriptor> PromoteStage(
        string stageId,
        IReadOnlyList<PackageUiSnapshotDescriptor> descriptors)
    {
        sessions.RemoveStage(stageId);
        return snapshots.PromoteStage(stageId, descriptors);
    }

    public void DiscardSnapshots(IEnumerable<PackageUiSnapshotDescriptor> descriptors)
        => snapshots.RemoveSnapshots(descriptors);

    public void DiscardStage(string stageId)
    {
        sessions.RemoveStage(stageId);
        snapshots.RemoveStage(stageId);
    }

    public void ScheduleCacheGarbageCollection() => snapshots.ScheduleGarbageCollection();

    public async Task<string?> TryResolveAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
        => sessions.State.TryResolvePackageAssetPath(packageId, assetPath)
            ?? await installedPackages.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);
}
