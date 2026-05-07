using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageActivationResult(
    bool Success,
    ActiveLoadedDevPackage? LoadedPackage,
    SessionPackageDescriptor SessionPackage);

internal sealed record DevPackageLoadSessionResult(
    ActiveDevPackageSession? Session,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
