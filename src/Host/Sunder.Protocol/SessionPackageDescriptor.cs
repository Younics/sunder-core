namespace Sunder.Protocol;

public sealed record SessionPackageDescriptor(
    string PackageId,
    string DisplayName,
    string Version,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    PackageReadinessState Readiness,
    IReadOnlyList<PackageViewDescriptor> Views,
    PackageFailureOrigin? FailureOrigin,
    string? LastError,
    DateTimeOffset? LastFailureAtUtc,
    int FailureCount);
