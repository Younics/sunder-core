using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class RegistrySourceAdoptionSelection
{
    // Shared by read-only prerequisite inspection and the separately confirmed adoption mutation.
    internal static RuntimeRegistryPackageBatchRequest Create(RuntimeRegistrySourceAdoptionRequest request, InstalledPackageRecord package)
    {
        var version = string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim();
        var tag = string.IsNullOrWhiteSpace(request.Tag) ? null : request.Tag.Trim();
        if (!string.Equals(request.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)
            || (version is null) == (tag is null) || version is not null && request.IncludePrerelease)
            throw new RuntimeValidationException("Source adoption requires one installed package and exactly one version or tag; prerelease inclusion is a tag policy.");
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        return new(origin.AbsoluteUri, [new(package.PackageId, version, request.DesiredTargets ?? [], tag)],
            request.IncludePrerelease, request.AllowDowngrade, Reinstall: true,
            CapabilityConsents: request.CapabilityConsents, ExpectedResolutionFingerprint: request.ExpectedResolutionFingerprint)
        { RuntimePrerequisiteExpectation = request.RuntimePrerequisiteExpectation };
    }
}
