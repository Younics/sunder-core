namespace Sunder.Protocol;

public enum PackageStoreMutationKind
{
    Install = 0,
    Upgrade = 1,
    Enable = 2,
    Disable = 3,
    Uninstall = 4,
}

public sealed record PackageStoreMutationRequest(
    PackageStoreMutationKind Kind,
    string? PackageId = null,
    string? PackagePath = null,
    bool AllowDowngrade = false,
    bool Reinstall = false);

public sealed record PackageStoreStageRequest(
    IReadOnlyList<PackageStoreMutationRequest> Mutations);

public sealed record PackageStoreStageResult(
    string? StageId,
    PackageOperationResult OperationResult,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    IReadOnlyList<PackageSourceDescriptor> PackageSources)
{
    public bool Success => OperationResult.Success && !string.IsNullOrWhiteSpace(StageId);

    public IReadOnlyList<string> ImpactedPackageIds => OperationResult.ImpactedPackageIds;

    public IReadOnlyList<string> Warnings => OperationResult.Warnings;

    public IReadOnlyList<string> Errors => OperationResult.Errors;

    public static PackageStoreStageResult Failed(
        string message,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null,
        IReadOnlyList<PackageSourceDescriptor>? packageSources = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? impactedPackageIds = null)
        => new(
            null,
            new PackageOperationResult(
                false,
                message,
                RuntimeSessionApplied: false,
                RequiresAppRestart: false,
                warnings ?? [],
                errors ?? [message])
            {
                ImpactedPackageIds = impactedPackageIds ?? [],
            },
            activePackages ?? [],
            packageSources ?? []);
}
