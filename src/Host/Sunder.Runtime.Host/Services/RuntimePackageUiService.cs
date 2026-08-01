using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageUiService(
    RuntimeSessionOwner sessions,
    PackageUiSnapshotStore snapshots,
    InstalledPackageStore installedPackages,
    PackageSessionLifecycleService? packageSessions = null,
    InstalledPackageLifecycleService? installedLifecycle = null)
{
    public IReadOnlyList<PackageUiSnapshotDescriptor> GetActiveSnapshots(string appRid)
    {
        var key = CreateAppTargetKey(appRid);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generation = sessions.GetSnapshot().SessionGeneration;
            var sources = ResolveTargets(sessions.State.GetAppPackageSources(), key);
            var created = snapshots.CreateSnapshots(sources, generation);
            if (sessions.Generation == generation)
            {
                return created;
            }
            snapshots.RemoveSnapshots(created);
        }
        throw new InvalidOperationException("The Runtime package generation changed while App snapshots were being materialized.");
    }

    public IReadOnlyList<PackageUiSnapshotDescriptor> GetStageSnapshots(string stageId, string appRid)
    {
        var key = CreateAppTargetKey(appRid);
        if (!sessions.TryGetStageGeneration(stageId, out var generation))
        {
            throw new KeyNotFoundException($"Package stage '{stageId}' was not found or is stale.");
        }
        var session = packageSessions?.GetStagedSession(stageId)
            ?? installedLifecycle?.GetStagedSession(stageId)
            ?? throw new KeyNotFoundException($"Package stage '{stageId}' does not have a package session candidate.");
        var created = snapshots.CreateSnapshots(
            ResolveTargets(session.GetAppPackageSources(), key),
            generation,
            stageId);
        if (sessions.TryGetStageGeneration(stageId, out var currentGeneration)
            && currentGeneration == generation)
        {
            return created;
        }
        snapshots.RemoveSnapshots(created);
        throw new KeyNotFoundException($"Package stage '{stageId}' became stale while App snapshots were being materialized.");
    }

    public PackageUiSnapshotLease? AcquireCurrent(string snapshotId)
        => snapshots.Acquire(snapshotId, sessions.GetSnapshot().SessionGeneration, stageId: null);

    public PackageUiSnapshotLease? AcquireStage(string stageId, string snapshotId)
        => sessions.TryGetStageGeneration(stageId, out var generation)
            ? snapshots.Acquire(snapshotId, generation, stageId)
            : null;

    public void ScheduleCacheGarbageCollection() => snapshots.ScheduleGarbageCollection();

    public async Task<string?> TryResolveAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
        => sessions.State.TryResolvePackageAssetPath(packageId, assetPath)
            ?? await installedPackages.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);

    private static SunderPackageTargetKey CreateAppTargetKey(string appRid)
    {
        if (!SunderPackageFormat.IsRuntimeIdentifier(appRid))
        {
            throw new InvalidDataException(
                $"App snapshot RID '{appRid}' is unsupported. An exact one of {string.Join(", ", SunderPackageFormat.SupportedRuntimeIdentifiers)} is required.");
        }
        return new SunderPackageTargetKey(SunderPackageFormat.AppHostRole, appRid);
    }

    private static IReadOnlyList<RuntimePackageTargetSource> ResolveTargets(
        IReadOnlyList<RuntimePackageSource> sources,
        SunderPackageTargetKey key)
    {
        var resolved = new List<RuntimePackageTargetSource>(sources.Count);
        foreach (var source in sources)
        {
            var manifest = source.Manifest
                ?? throw new InvalidDataException($"Package source '{source.PackageId}' is missing its strict manifest.");
            if (!SunderPackageTargetResolver.TryResolveTarget(manifest, key, out var target))
            {
                throw new InvalidDataException(
                    $"Package '{source.PackageId}' does not declare the exact App target '{key}'. RID fallback is not supported.");
            }
            var exactTarget = target
                ?? throw new InvalidDataException($"Package target resolver returned no metadata for '{key}'.");
            if (exactTarget.Kind is not (SunderPackageFormat.AvaloniaTargetKind or SunderPackageFormat.WebTargetKind))
            {
                throw new InvalidDataException(
                    $"Package '{source.PackageId}' exact App target '{key}' has unsupported target kind '{exactTarget.Kind}'. This App supports '{SunderPackageFormat.AvaloniaTargetKind}' and '{SunderPackageFormat.WebTargetKind}' targets.");
            }
            var compatibilityErrors = SunderSdkCompatibilityProfile.Validate(source.PackageId, key, exactTarget);
            if (compatibilityErrors.Count > 0)
            {
                throw new InvalidDataException(string.Join(" | ", compatibilityErrors));
            }
            resolved.Add(new RuntimePackageTargetSource(source, key, exactTarget));
        }
        return resolved;
    }
}
