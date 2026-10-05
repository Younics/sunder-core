using Microsoft.Extensions.Logging;
using Sunder.Package.Projections;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageProjectionMaintenance(
    PackageProjectionCache projections,
    InstalledPackageStore installedPackages,
    ILogger<PackageProjectionMaintenance> logger)
{
    public async Task<PackageProjectionGarbageCollectionResult> CollectGarbageAsync(
        CancellationToken cancellationToken)
    {
        var installed = await installedPackages.ListAsync(cancellationToken).ConfigureAwait(false);
        var reachable = installed
            .Select(static package => package.ContentIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var result = await projections.CollectGarbageAsync(reachable, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Projection cache recovery/GC completed: {ActiveLeaseCount} active leases, {RecoveredLeaseCount} stale leases recovered, {MarkedEntryCount} marked, {TombstonedEntryCount} tombstoned, {PreservedEntryCount} preserved",
            result.ActiveLeaseCount,
            result.RecoveredLeaseCount,
            result.MarkedEntryCount,
            result.TombstonedEntryCount,
            result.PreservedEntryCount);
        return result;
    }
}
