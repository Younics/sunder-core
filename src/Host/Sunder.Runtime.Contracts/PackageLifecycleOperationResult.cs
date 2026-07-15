namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleOperationResult(
    bool Success,
    string? Message,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    IReadOnlyList<PackageUiSnapshotDescriptor> PackageUiSnapshots,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds)
{
    public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; }
        = RuntimeContractCollections.Freeze(ActivePackages.Select(
            package => package with { Views = RuntimeContractCollections.Freeze(package.Views) }));

    public IReadOnlyList<PackageUiSnapshotDescriptor> PackageUiSnapshots { get; }
        = RuntimeContractCollections.Freeze(PackageUiSnapshots);

    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);

    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);

    public IReadOnlyList<string> ImpactedPackageIds { get; }
        = RuntimeContractCollections.Freeze(ImpactedPackageIds);

    public RuntimePackageStamp? CommittedStamp { get; init; }

    public static PackageLifecycleOperationResult Failed(
        string message,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null,
        IReadOnlyList<PackageUiSnapshotDescriptor>? packageUiSnapshots = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? impactedPackageIds = null)
        => new(false, message, activePackages ?? [], packageUiSnapshots ?? [], warnings ?? [], errors ?? [message], impactedPackageIds ?? []);
}
