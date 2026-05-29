namespace Sunder.Protocol;

public sealed record PackageInstallFromPathRequest(
    string PackagePath,
    bool ApplyRuntimeSession = true);
