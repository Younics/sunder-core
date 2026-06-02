namespace Sunder.Protocol;

public sealed record PackageInstallBatchFromPathRequest(
    IReadOnlyList<PackageInstallBatchFromPathItem> Items);

public sealed record PackageInstallBatchFromPathItem(
    string PackagePath,
    string? PackageId = null,
    bool AllowDowngrade = false,
    bool Reinstall = false);
