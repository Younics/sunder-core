namespace Sunder.Protocol;

public sealed record DevPackageLoadResult(
    IReadOnlyList<ActivePackageDescriptor> LoadedPackages,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
