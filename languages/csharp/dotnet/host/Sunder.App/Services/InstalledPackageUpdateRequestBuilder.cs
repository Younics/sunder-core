using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal static class InstalledPackageUpdateRequestBuilder
{
    public static IReadOnlyList<RuntimeRegistryPackageBatchRequest> Build(
        IReadOnlyList<InstalledPackageDescriptor> installedPackages)
        => installedPackages
            .Where(package => package.Provenance is
            {
                SourceKind: InstalledPackageSourceKind.Registry,
                VersionPolicy: InstalledPackageVersionPolicy.FollowTag,
                RegistryOrigin: not null,
                RequestedTag: not null,
            })
            .GroupBy(package => (package.Provenance.RegistryOrigin!, package.Provenance.IncludePrerelease))
            .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
            .ThenBy(group => group.Key.IncludePrerelease)
            .Select(group => new RuntimeRegistryPackageBatchRequest(
                group.Key.Item1,
                group.Select(package => new RuntimeRegistryPackageChangeRequest(
                        package.Provenance.SourcePackageId ?? package.PackageId,
                        null,
                        [],
                        package.Provenance.RequestedTag,
                        package.Provenance.VersionRange))
                    .ToArray(),
                group.Key.IncludePrerelease))
            .ToArray();
}
