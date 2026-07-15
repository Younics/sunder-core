namespace Sunder.Runtime.Contracts;

public sealed record SessionPackageDescriptor(
    string PackageId,
    string DisplayName,
    string Version,
    PackageHostRoles HostRoles,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    PackageReadinessState Readiness,
    IReadOnlyList<PackageViewDescriptor> Views,
    PackageFailureOrigin? FailureOrigin,
    string? LastError,
    DateTimeOffset? LastFailureAtUtc,
    int FailureCount);
