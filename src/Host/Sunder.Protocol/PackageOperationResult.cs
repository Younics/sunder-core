namespace Sunder.Protocol;

public sealed record PackageOperationResult(
    bool Success,
    string? Message,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> ImpactedPackageIds { get; init; } = [];
}
