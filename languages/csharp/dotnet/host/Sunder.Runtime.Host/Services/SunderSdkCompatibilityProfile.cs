using Sunder.Package.Format;
using Sunder.Package.Hosting;

namespace Sunder.Runtime.Host.Services;

internal static class SunderSdkCompatibilityProfile
{
    internal static IReadOnlyList<PackageImplementationProfile> SupportedProfiles => PackageHostCompatibility.SupportedProfiles;
    internal static IReadOnlyDictionary<string, bool> WorkerV1CapabilityClassification => PackageHostCompatibility.WorkerV1CapabilityClassification;

    public static IReadOnlyList<string> Validate(string? packageId, SunderPackageTargetKey targetKey, SunderPackageTargetManifest target)
        => PackageHostCompatibility.Validate(packageId, targetKey, target);

    internal static IReadOnlyList<string> Validate(string? packageId, SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target, IReadOnlyList<PackageImplementationProfile> profiles)
        => PackageHostCompatibility.Validate(packageId, targetKey, target, profiles);
}
