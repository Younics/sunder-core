namespace Sunder.Protocol;

public sealed record ActivePackageDescriptor(
    string PackageId,
    string DisplayName,
    string Version,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    PackageReadinessState Readiness,
    IReadOnlyList<PackageViewDescriptor> Views);
