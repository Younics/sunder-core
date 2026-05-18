namespace Sunder.Protocol;

public sealed record PackageSessionStatus(
    string PackageId,
    string? DisplayName,
    string? Version,
    PackageSourceKind ActiveSourceKind,
    bool IsLoaded,
    bool WatchEnabled,
    bool OverridesInstalledPackage,
    PackageReadinessState? Readiness,
    string? ErrorMessage);
