using System.Text.Json.Serialization;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record InstalledPackagePolicyRecord(
    string State,
    string? ErrorCode,
    string? RegistryOrigin,
    RegistryPackageVersionIdentity Package,
    IReadOnlyList<PackagePolicyOriginFile> Snapshots)
{
    public IReadOnlyList<PackagePolicyOriginFile> Snapshots { get; } = Array.AsReadOnly(Snapshots.ToArray());
    [JsonIgnore] public bool IsBlocked => State != "Allowed";

    internal InstalledPackageRecord ApplyTo(InstalledPackageRecord package) => package with
    {
        Policy = this,
        ExecutionRuntimeBindings = package.ExecutionRuntimeBindings.Select(binding =>
        {
            if (binding.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development) return binding;
            var subject = PackagePolicyAdmission.RuntimeSubject(RuntimePackageExecutionRuntimeBinding.FromInstalled(binding));
            var origin = Snapshots.SingleOrDefault(value => value.Origin == RegistryActivePolicyClient.Origin(binding.RegistryOrigin));
            if (origin is null || !origin.Runtimes.TryGetValue(RegistryActivePolicyClient.RuntimeKey(subject), out var entry)
                || entry.Revision < binding.RuntimePolicyRevision || entry.Decision.Disposition == "PolicyRequired") return binding;
            if (binding.RuntimeLifecycleState == "revoked" && entry.Decision.LifecycleState != "revoked"
                || binding.RuntimeLifecycleState == "retired" && entry.Decision.LifecycleState is "supported" or "deprecated") return binding;
            return binding with
            {
                RuntimePolicyRevision = entry.Revision,
                RuntimeLifecycleState = entry.Decision.LifecycleState,
                PolicyObservedAtUtc = entry.Lease.ObservedAtUtc!.Value,
            };
        }).ToArray(),
    };

    internal void Validate(InstalledPackageRecord installed)
    {
        RegistryActivePolicyClient.Require(Package == Subject(installed)
            && (State == "Allowed" && ErrorCode is null || State is "PolicyBlocked" or "PolicyRequired" && ErrorCode is not null)
            && Snapshots.Count <= 8 && Snapshots.Select(value => value.Origin).Distinct(StringComparer.Ordinal).Count() == Snapshots.Count,
            "Installed package policy identity/state is invalid.");
        if (RegistryOrigin is not null) RegistryActivePolicyClient.Require(RegistryActivePolicyClient.Origin(RegistryOrigin) == RegistryOrigin, "Installed package policy origin is not canonical.");
        if (installed.Provenance.SourceKind == InstalledPackageSourceKind.Registry)
            RegistryActivePolicyClient.Require(RegistryOrigin == RegistryActivePolicyClient.Origin(installed.Provenance.RegistryOrigin!), "Installed policy origin differs from package provenance.");
        foreach (var snapshot in Snapshots) PackageLifecyclePolicyCache.ValidateRetainedSnapshot(snapshot);
        if (State == "Allowed")
        {
            var authority = Snapshots.SingleOrDefault(value => value.Origin == RegistryOrigin);
            RegistryActivePolicyClient.Require(authority is not null
                && authority.Packages.TryGetValue(RegistryActivePolicyClient.PackageKey(Package), out var entry)
                && entry.Decision.Disposition == "Allowed" && entry.Lease.State == RuntimePolicyLeaseStates.Valid,
                "Allowed installed package policy lacks exact lease evidence.");
            foreach (var binding in installed.ExecutionRuntimeBindings.Where(value => value.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.Development))
            {
                var runtime = PackagePolicyAdmission.RuntimeSubject(RuntimePackageExecutionRuntimeBinding.FromInstalled(binding));
                RegistryActivePolicyClient.Require(Snapshots.SingleOrDefault(value => value.Origin == RegistryActivePolicyClient.Origin(binding.RegistryOrigin)) is { } origin
                    && origin.Runtimes.TryGetValue(RegistryActivePolicyClient.RuntimeKey(runtime), out var runtimeEntry)
                    && runtimeEntry.Revision >= binding.RuntimePolicyRevision && runtimeEntry.Decision.Disposition == "Allowed"
                    && binding.RuntimeLifecycleState != "revoked" && runtimeEntry.Lease.State == RuntimePolicyLeaseStates.Valid,
                    "Allowed installed policy lacks exact runtime evidence.");
            }
            RegistryActivePolicyClient.Require(Snapshots.All(snapshot => !snapshot.ClockInvalidated
                && snapshot.Packages.Values.All(value => value.Decision.Disposition == "Allowed" && value.Lease.State == RuntimePolicyLeaseStates.Valid)
                && snapshot.Platforms.Values.All(value => value.Decision.Disposition == "Allowed" && value.Lease.State == RuntimePolicyLeaseStates.Valid)
                && snapshot.Runtimes.Values.All(value => value.Decision.Disposition == "Allowed" && value.Lease.State == RuntimePolicyLeaseStates.Valid)),
                "Allowed installed policy contains a denial or invalidated lease.");
        }
    }

    internal static RegistryPackageVersionIdentity Subject(InstalledPackageRecord package) => new(package.PackageId, package.Version,
        package.Provenance.SourceKind == InstalledPackageSourceKind.Registry ? package.Provenance.SourceIdentity! : package.SourcePackageContentIdentity ?? package.ContentIdentity);
}
