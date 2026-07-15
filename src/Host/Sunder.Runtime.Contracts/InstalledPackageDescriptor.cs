namespace Sunder.Runtime.Contracts;

public sealed record InstalledPackageDescriptor(
    string PackageId,
    string Name,
    string Version,
    PackageHostRoles HostRoles,
    string? Summary,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    IReadOnlyList<PackageDependencyDescriptor> DependsOn,
    DateTimeOffset InstalledAtUtc,
    string? StatusMessage);
