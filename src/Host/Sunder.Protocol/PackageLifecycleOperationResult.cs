namespace Sunder.Protocol;

public sealed record PackageLifecycleOperationResult(
    bool Success,
    string? Message,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    IReadOnlyList<PackageSourceDescriptor> PackageSources,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds)
{
    public static PackageLifecycleOperationResult Failed(
        string message,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null,
        IReadOnlyList<PackageSourceDescriptor>? packageSources = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? impactedPackageIds = null)
        => new(false, message, activePackages ?? [], packageSources ?? [], warnings ?? [], errors ?? [message], impactedPackageIds ?? []);
}
