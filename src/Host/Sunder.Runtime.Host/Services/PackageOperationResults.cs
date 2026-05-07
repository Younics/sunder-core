using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal static class PackageOperationResults
{
    public static PackageOperationResult Success(
        string message,
        bool requiresAppRestart = true,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? impactedPackageIds = null)
        => new(true, message, requiresAppRestart, warnings ?? [], [])
        {
            ImpactedPackageIds = impactedPackageIds ?? [],
        };

    public static PackageOperationResult Failure(string message, IReadOnlyList<string>? errors = null, IReadOnlyList<string>? warnings = null)
        => new(false, message, RequiresAppRestart: false, warnings ?? [], errors ?? [message]);
}
