namespace Sunder.Protocol;

public sealed record PackageSessionOperationResult(
    bool Success,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    PackageSessionStatus? Status)
{
    public static PackageSessionOperationResult Failed(string message, IReadOnlyList<string>? errors = null, IReadOnlyList<string>? warnings = null)
        => new(false, message, warnings ?? [], errors ?? [message], [], null);
}
