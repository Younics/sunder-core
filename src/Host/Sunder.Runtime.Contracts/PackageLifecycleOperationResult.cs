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
    public static PackageLifecycleOperationResult Failed(
        string message,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null,
        IReadOnlyList<PackageUiSnapshotDescriptor>? packageUiSnapshots = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? impactedPackageIds = null)
        => new(false, message, activePackages ?? [], packageUiSnapshots ?? [], warnings ?? [], errors ?? [message], impactedPackageIds ?? []);
}
