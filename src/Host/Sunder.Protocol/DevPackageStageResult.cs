namespace Sunder.Protocol;

public sealed record DevPackageStageResult(
    string? StageId,
    IReadOnlyList<ActivePackageDescriptor> LoadedPackages,
    IReadOnlyList<PackageSourceDescriptor> PackageSources,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
