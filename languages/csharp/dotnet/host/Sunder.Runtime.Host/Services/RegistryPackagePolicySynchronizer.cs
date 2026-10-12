using System.Collections.Concurrent;
using Sunder.Registry.Contracts;

namespace Sunder.Runtime.Host.Services;

/// <summary>Explicit network boundary; activation and RPC admission only read the cache.</summary>
internal sealed class RegistryPackagePolicySynchronizer(RegistryActivePolicyClient client, PackageLifecyclePolicyCache cache) : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _origins = new(StringComparer.Ordinal);

    public async Task RefreshAsync(IReadOnlyList<PackagePolicySubjects> subjects, CancellationToken token)
    {
        foreach (var group in subjects.GroupBy(value => RegistryActivePolicyClient.Origin(value.Origin), StringComparer.Ordinal))
        {
            var gate = _origins.GetOrAdd(group.Key, _ => new(1));
            await gate.WaitAsync(token);
            try
            {
                foreach (var batch in Batches(group.Key, group))
                {
                    var request = await cache.CreateRequestAsync(group.Key, batch.Packages, batch.Platforms, token);
                    await cache.ObserveAsync(await client.SynchronizeAsync(group.Key, request, token), token);
                }
            }
            finally { gate.Release(); }
        }
    }

    internal async Task RefreshPlanAsync(string origin, RegistryResolveInstallPlanResponse plan, CancellationToken token)
    {
        var policy = plan.ResolutionPolicy ?? throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyRequired);
        if (plan.PackagePolicy != policy.PackagePolicy || plan.PolicyCheckpoint != policy.Checkpoint)
            throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyConflict);
        var packages = plan.Items.Where(value => value.ActionKind != RegistryPackageActionKinds.PackageRemove)
            .Select(value => new RegistryPackageVersionIdentity(value.PackageId, value.Version!, value.PackageContentIdentity)).ToArray();
        var platforms = policy.PlatformCatalogs.Select(value => value.Catalog).ToArray();
        if (packages.Length + platforms.Length == 0) return;
        // The plan is not lease authority. A nonce-bound refresh must still agree with
        // the exact package revision used by resolution before it can be used.
        await RefreshAsync([new(origin, packages, platforms)], token);
        cache.RequirePlan(origin, policy.PackagePolicy, policy.Checkpoint);
        foreach (var package in packages) cache.RequireActivation(origin, package);
    }

    internal static IEnumerable<PackagePolicySubjects> Batches(string origin, IEnumerable<PackagePolicySubjects> subjects)
    {
        var packages = new HashSet<RegistryPackageVersionIdentity>();
        var platforms = new HashSet<RegistryPlatformCatalogIdentity>();
        foreach (var item in subjects)
        {
            RegistryActivePolicyClient.Require(item.Packages.Count <= RegistryPolicyLimits.MaximumPackageSubjects
                && item.Platforms.Count <= RegistryPolicyLimits.MaximumPlatformSubjects,
                "One complete policy subject group exceeds the request bound.");
            if (packages.Union(item.Packages).Count() > RegistryPolicyLimits.MaximumPackageSubjects
                || platforms.Union(item.Platforms).Count() > RegistryPolicyLimits.MaximumPlatformSubjects)
            {
                yield return new(origin, packages.ToArray(), platforms.ToArray());
                packages.Clear(); platforms.Clear();
            }
            packages.UnionWith(item.Packages); platforms.UnionWith(item.Platforms);
        }
        if (packages.Count + platforms.Count > 0)
            yield return new(origin, packages.ToArray(), platforms.ToArray());
    }

    public void Dispose() { foreach (var gate in _origins.Values) gate.Dispose(); }
}
