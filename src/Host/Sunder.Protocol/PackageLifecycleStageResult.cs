namespace Sunder.Protocol;

public sealed record PackageLifecycleStageResult(
    string? StageId,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    IReadOnlyList<PackageSourceDescriptor> PackageSources,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds)
{
    public bool Success => StageId is not null && Errors.Count == 0;

    public static PackageLifecycleStageResult Failed(
        string message,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null,
        IReadOnlyList<PackageSourceDescriptor>? packageSources = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null)
        => new(null, activePackages ?? [], packageSources ?? [], warnings ?? [], errors ?? [message], []);
}
