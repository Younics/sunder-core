namespace Sunder.Protocol;

public sealed record PackageOperationResult(
    bool Success,
    string? Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool AppShellApplied { get; init; }

    public IReadOnlyList<string> ImpactedPackageIds { get; init; } = [];
}
