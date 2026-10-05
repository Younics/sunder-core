using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackagePolicySubjects(string Origin, IReadOnlyList<RegistryPackageVersionIdentity> Packages,
    IReadOnlyList<RegistryPlatformCatalogIdentity> Platforms, IReadOnlyList<RegistryRuntimePolicySubject> Runtimes);

internal sealed class PackagePolicyAdmission(PackageLifecyclePolicyCache cache)
{
    public async Task RequireAsync(RuntimePackageSource source, RuntimePackageExecutionRuntimeBinding? binding, CancellationToken token)
    {
        if (source.InstalledPolicy is { } retained) await cache.RestoreRetainedAsync(retained.Snapshots, token);
        if (source.Kind == PackageSourceKind.Installed && source.RegistryPolicyOrigin is null
            && binding?.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.Development)
            await cache.LoadKnownOriginsAsync(token);
        var subjects = Subjects(source, binding);
        foreach (var origin in subjects.Select(value => value.Origin).Distinct(StringComparer.Ordinal))
            await cache.CheckpointClockAsync(origin, token);
        Require(source, binding);
    }

    public void Require(RuntimePackageSource source, RuntimePackageExecutionRuntimeBinding? binding)
    {
        Require(Subjects(source, binding));
        if (binding is not null && binding.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.Development)
            RequireBindingPolicy(cache, binding);
    }

    internal static RegistryRuntimePolicyDecision RequireBindingPolicy(PackageLifecyclePolicyCache cache, RuntimePackageExecutionRuntimeBinding binding)
    {
        if (binding.RuntimeLifecycleState == "revoked") throw new PackagePolicyDeniedException(ExecutionRuntimeErrorCodes.Revoked);
        var decision = cache.RuntimeDecision(binding.RegistryOrigin, RuntimeSubject(binding), binding.RuntimePolicyRevision);
        if (binding.RuntimeLifecycleState == "retired" && decision.LifecycleState is "supported" or "deprecated")
            throw new PackagePolicyDeniedException(ExecutionRuntimeErrorCodes.Retired);
        return decision;
    }

    private void Require(IReadOnlyList<PackagePolicySubjects> subjects)
    {
        foreach (var group in subjects)
        {
            foreach (var package in group.Packages) cache.RequireActivation(group.Origin, package, []);
            if (group.Platforms.Count > 0) cache.RequirePlatformActivation(group.Origin, group.Platforms);
            if (group.Runtimes.Count > 0) cache.RequireRuntimeActivation(group.Origin, group.Runtimes);
        }
    }

    internal IReadOnlyList<PackagePolicySubjects> Subjects(RuntimePackageSource source, RuntimePackageExecutionRuntimeBinding? binding)
    {
        var result = new List<PackagePolicySubjects>();
        var development = binding?.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development;
        if (source.Kind == PackageSourceKind.Dev && source.Manifest is { } local && PackageCompatibilityAuthority.NeedsEvidence(local))
        {
            var imports = source.DevelopmentImports ?? throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyRequired);
            var platforms = DevelopmentImportAuthorityStore.Platforms(local);
            if (platforms.Length > 0 && !imports.IsLocal) result.Add(new(imports.RegistryOrigin!, [], platforms, []));
        }
        if (source.Kind == PackageSourceKind.Installed && (source.RegistryPolicyOrigin is not null || !development))
        {
            var subject = new RegistryPackageVersionIdentity(source.PackageId, source.Manifest?.Version
                ?? throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyRequired),
                source.RegistryPolicyContentIdentity ?? source.ContentIdentity
                ?? throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyRequired));
            // Local imports can only reuse an already loaded exact policy identity. Runtime
            // artifact provenance must never silently become package provenance.
            var origin = source.RegistryPolicyOrigin ?? cache.FindPackageOrigin(subject)
                ?? throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.PolicyRequired);
            result.Add(new(RegistryActivePolicyClient.Origin(origin), [subject], Platforms(source.Manifest!), []));
        }
        if (binding is not null && !development)
            result.Add(new(RegistryActivePolicyClient.Origin(binding.RegistryOrigin), [], [], [RuntimeSubject(binding)]));
        return result;
    }

    internal static RuntimePackageSource WithProvenance(RuntimePackageSource source, InstalledPackageRecord package)
        => source with
        {
            RegistryPolicyOrigin = package.Provenance.SourceKind == InstalledPackageSourceKind.Registry ? package.Provenance.RegistryOrigin : package.Policy?.RegistryOrigin,
            RegistryPolicyContentIdentity = InstalledPackagePolicyRecord.Subject(package).PackageContentIdentity,
            InstalledPolicy = package.Policy,
            Compatibility = package.Compatibility,
            AcquiredTargets = InstalledPackageProjection.Keys(package),
            HostRoles = source.Manifest is null ? source.HostRoles : PackageTargetSelection.GetHostRoles(source.Manifest, InstalledPackageProjection.Keys(package)),
            Dependencies = package.DependsOn.Select(value => new PackageDependencyDescriptor(value.PackageId, value.VersionRange)).ToArray(),
        };

    internal static RegistryPlatformCatalogIdentity[] Platforms(SunderPackageManifest manifest) => (manifest.ContractBundles ?? [])
        .Where(value => value?.AuthorityKind == "platform")
        .Select(value => new RegistryPlatformCatalogIdentity(value!.AuthorityId!, value.SelectedAuthorityRelease!, value.SelectedAuthorityContentIdentity!))
        .Distinct().OrderBy(RegistryActivePolicyClient.PlatformKey, StringComparer.Ordinal).ToArray();

    internal static RegistryRuntimePolicySubject RuntimeSubject(RuntimePackageExecutionRuntimeBinding binding)
        => new(new(binding.RuntimeId, binding.RuntimeVersion, binding.RuntimePackagingRevision, binding.Rid, binding.ModuleAbi),
            binding.RuntimeManifestSha256, binding.RuntimeArtifactSha256);
}
