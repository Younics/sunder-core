namespace Sunder.Runtime.Contracts;

public sealed record ActivePackageDescriptor(
    string PackageId,
    string DisplayName,
    string Version,
    PackageHostRoles HostRoles,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    PackageReadinessState Readiness,
    IReadOnlyList<PackageViewDescriptor> Views);
