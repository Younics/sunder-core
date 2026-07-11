using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageUiService(
    RuntimeSessionOwner sessions,
    PackageUiSnapshotStore snapshots,
    InstalledPackageStore installedPackages)
{
    public IReadOnlyList<PackageUiSnapshotDescriptor> GetActiveSnapshots()
        => snapshots.CreateSnapshots(sessions.State.GetActivePackageSources(), sessions.Generation);

    public IReadOnlyList<PackageUiSnapshotDescriptor> CreateSnapshots(
        IReadOnlyList<RuntimePackageSource> sources,
        long generation,
        string? stageId = null)
        => snapshots.CreateSnapshots(sources, generation, stageId);

    public PackageUiSnapshotLease? AcquireCurrent(string snapshotId)
        => snapshots.Acquire(snapshotId, sessions.Generation, stageId: null);

    public PackageUiSnapshotLease? AcquireStage(string stageId, string snapshotId)
        => sessions.TryGetStageGeneration(stageId, out var generation)
            ? snapshots.Acquire(snapshotId, generation, stageId)
            : null;

    public void CommitStage(string stageId)
    {
        sessions.RemoveStage(stageId);
        snapshots.RemoveStage(stageId);
        snapshots.RemoveOlderGenerations(sessions.Generation);
    }

    public void DiscardStage(string stageId)
    {
        sessions.RemoveStage(stageId);
        snapshots.RemoveStage(stageId);
    }

    public async Task<string?> TryResolveAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
        => sessions.State.TryResolvePackageAssetPath(packageId, assetPath)
            ?? await installedPackages.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);
}
