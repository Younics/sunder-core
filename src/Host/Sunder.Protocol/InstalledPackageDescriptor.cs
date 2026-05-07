namespace Sunder.Protocol;

public sealed record InstalledPackageDescriptor(
    string PackageId,
    string Name,
    string Version,
    string? Summary,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    IReadOnlyList<PackageDependencyDescriptor> DependsOn,
    DateTimeOffset InstalledAtUtc,
    string? StatusMessage);
