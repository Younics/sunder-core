using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageActivationResult(
    bool Success,
    ActiveLoadedPackage? LoadedPackage,
    SessionPackageDescriptor SessionPackage);

internal sealed record PackageSessionLoadResult(
    ActivePackageSession? Session,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
