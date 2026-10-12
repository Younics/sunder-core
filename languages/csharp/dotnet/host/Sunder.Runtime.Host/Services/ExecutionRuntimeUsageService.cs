using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeUsageService(InstalledPackageStore installed, PackageStoreCoordinator packages,
    RuntimeSessionOwner sessions, ExecutionRuntimeStore store, ExecutionRuntimeManagementState state,
    AppProcessPolicySessions? appProcesses = null)
{
    // Caller owns RuntimeOperationGate. Include process ownership, not just displayed Active state.
    internal async Task<RuntimeExecutionRuntimeUsage> CaptureAsync(IReadOnlyList<RuntimeExecutionRuntimeIdentity> runtimes, CancellationToken token,
        IReadOnlyCollection<string>? bindingPackages = null)
    {
        var identities = runtimes.ToHashSet();
        var catalog = await installed.ListAsync(token);
        var references = catalog.SelectMany(package => package.ExecutionRuntimeBindings
            .Where(binding => identities.Contains(binding.Runtime))
            .Select(binding => new RuntimeExecutionRuntimePackageReference(package.PackageId, package.Version, package.ContentIdentity, binding.Role, binding.Rid)))
            .OrderBy(value => value.PackageId, StringComparer.Ordinal).ThenBy(value => value.Role, StringComparer.Ordinal).ToArray();
        var affected = (bindingPackages ?? references.Select(value => value.PackageId).ToArray()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PackageStorePolicy.AddImpactedDependents(affected, catalog);
        var runtimeUsage = await store.CaptureManagementUsageAsync(identities.Select(Identity).ToArray(), token);
        if (bindingPackages is null) foreach (var lease in runtimeUsage.Leases) affected.Add(lease.PackageId);
        var affectedLeases = runtimeUsage.Leases.Where(value => affected.Contains(value.PackageId)).ToArray();
        var running = sessions.State.ActiveSession.LoadedPackageMap.Keys.Where(affected.Contains)
            .Concat(affectedLeases.Select(value => value.PackageId)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var management = await state.ReadAsync(token);
        var pending = management.Updates.Where(ExecutionRuntimeManagementState.Pending)
            .Count(update => update.Runtimes.Any(value => identities.Contains(value.CurrentRuntime)
                || identities.Contains(value.Resolution.Artifact.Identity)));
        var app = bindingPackages is null && appProcesses?.CapturePolicySubjects().SelectMany(value => value.Packages).Any(value => affected.Contains(value.PackageId)) == true;
        var pendingPackages = await packages.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(token);
        var affectedIds = affected.Order(StringComparer.Ordinal).ToArray();
        var manual = management.ManualInstalls.Count(value => identities.Contains(value.Runtime));
        var fingerprint = Hash(new { references, affectedIds, running, runtimeUsage, manual, pending, app,
            PreparedStages = packages.HasPreparedStages, pendingPackages.PackageBindings });
        return new(fingerprint, references, affectedIds, running, affectedLeases.Length,
            runtimeUsage.PendingTransactions + pending + (packages.HasPreparedStages ? 1 : 0), runtimeUsage.Retentions.Count, manual, app);
    }
}
