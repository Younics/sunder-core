namespace Sunder.Protocol;

public sealed record PackageUpgradeFromPathRequest(
    string PackagePath,
    bool AllowDowngrade = false,
    bool Reinstall = false);
