namespace Sunder.Runtime.Contracts;

public sealed record PackageSessionOperationResult(
    bool Success,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    PackageSessionStatus? Status)
{
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);

    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);

    public IReadOnlyList<string> ImpactedPackageIds { get; }
        = RuntimeContractCollections.Freeze(ImpactedPackageIds);

    public RuntimePackageStamp? CommittedStamp { get; init; }

    public static PackageSessionOperationResult Failed(string message, IReadOnlyList<string>? errors = null, IReadOnlyList<string>? warnings = null)
        => new(false, message, warnings ?? [], errors ?? [message], [], null);
}
