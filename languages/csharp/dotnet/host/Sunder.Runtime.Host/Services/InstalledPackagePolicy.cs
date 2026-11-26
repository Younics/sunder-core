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

    internal InstalledPackageRecord ApplyTo(InstalledPackageRecord package) => package with { Policy = this };

    // A clock checkpoint or a renewed lease for the same decision does not
    // change an App comparison or uninstall preview. Admission still checks
    // the live lease, clock and authority at its own boundary.
    internal object ComparisonEvidence() => new
    {
        State, ErrorCode, RegistryOrigin, Package,
        Snapshots = Snapshots.OrderBy(snapshot => snapshot.Origin, StringComparer.Ordinal).Select(snapshot => new
        {
            snapshot.SchemaVersion, snapshot.Origin, snapshot.PackagePolicy, snapshot.ClockGeneration, snapshot.ClockInvalidated,
            Epoch = new { snapshot.HighWater.BootSessionId, snapshot.HighWater.Source, snapshot.HighWater.Frequency, snapshot.HighWater.VirtualMachineGeneration },
            Packages = snapshot.Packages.Select(pair => new
            {
                pair.Key, pair.Value.Revision, pair.Value.Decision,
                Lease = new { pair.Value.Lease.State, pair.Value.Lease.ErrorCode, pair.Value.Lease.Watermark },
            }).ToArray(),
            Platforms = snapshot.Platforms.Select(pair => new
            {
                pair.Key, pair.Value.Revision, pair.Value.Decision,
                Lease = new { pair.Value.Lease.State, pair.Value.Lease.ErrorCode, pair.Value.Lease.Watermark },
            }).ToArray(),
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
            RegistryActivePolicyClient.Require(Snapshots.All(snapshot => !snapshot.ClockInvalidated
                && snapshot.Packages.Values.All(value => value.Decision.Disposition == "Allowed" && value.Lease.State == RuntimePolicyLeaseStates.Valid)
                && snapshot.Platforms.Values.All(value => value.Decision.Disposition == "Allowed" && value.Lease.State == RuntimePolicyLeaseStates.Valid)),
                "Allowed installed policy contains a denial or invalidated lease.");
        }
    }

    internal static RegistryPackageVersionIdentity Subject(InstalledPackageRecord package) => new(package.PackageId, package.Version,
        package.Provenance.SourceKind == InstalledPackageSourceKind.Registry ? package.Provenance.SourceIdentity! : package.SourcePackageContentIdentity ?? package.ContentIdentity);
}
