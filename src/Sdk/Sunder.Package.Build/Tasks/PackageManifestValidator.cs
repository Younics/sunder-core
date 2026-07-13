using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

internal sealed class PackageManifestValidator(
    string packageVersion,
    string entryAssembly,
    string? sdkApiVersion,
    string? sdkPackageVersion,
    string projectDirectory,
    PackageAssetDiscovery assets,
    TaskLoggingHelper log)
{
    public bool Validate(PackageManifestMetadata metadata)
    {
        ValidatePackageId(metadata.Id, "package id");
        if (string.IsNullOrWhiteSpace(metadata.Name))
        {
            log.LogError("Sunder package metadata must include Name.");
        }
        if (!SemanticVersion.TryParse(packageVersion, out _))
        {
            log.LogError($"Sunder package version '{packageVersion}' must be strict SemVer 2.0.");
        }
        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            log.LogError("Sunder package entry assembly name is required.");
        }
        else
        {
            ValidateRelativePath(entryAssembly, "package entry assembly");
        }
        if (!string.IsNullOrWhiteSpace(sdkApiVersion)
            && (!int.TryParse(sdkApiVersion, out var parsedSdkApiVersion) || parsedSdkApiVersion != SunderSdkApiVersions.V1))
        {
            log.LogError($"Sunder SDK API version '{sdkApiVersion}' must be {SunderSdkApiVersions.V1} for the V1 manifest.");
        }
        if (!string.IsNullOrWhiteSpace(metadata.Icon))
        {
            ValidateRelativePath(metadata.Icon, "package icon");
            if (!assets.Exists(metadata.Icon))
            {
                log.LogError($"Sunder package icon '{metadata.Icon}' does not exist under '{projectDirectory}'.");
            }
        }

        var seenDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in metadata.Dependencies)
        {
            var packageId = dependency.PackageId ?? string.Empty;
            ValidatePackageId(packageId, "dependency package id");
            if (!seenDependencies.Add(packageId))
            {
                log.LogError($"Sunder package dependency '{packageId}' is declared more than once.");
            }
            if (!PackageVersionRange.TryParse(dependency.VersionRange, out _))
            {
                log.LogError($"Sunder package dependency '{packageId}' has unsupported VersionRange '{dependency.VersionRange}'.");
            }
        }
        if (!SemanticVersion.TryParse(sdkPackageVersion, out _))
        {
            log.LogError($"Sunder SDK package version '{sdkPackageVersion}' must be strict SemVer 2.0.");
        }
        foreach (var capability in metadata.RequiredSdkCapabilities)
        {
            if (!SunderPackageFormat.IsSdkCapabilityId(capability))
            {
                log.LogError($"Sunder SDK capability '{capability}' is not a valid V1 capability id.");
            }
        }
        return !log.HasLoggedErrors;
    }

    private void ValidatePackageId(string packageId, string label)
    {
        if (!PackageId.TryParse(packageId, out _))
        {
            log.LogError($"Sunder {label} '{packageId}' must use lowercase dot-separated ASCII identifiers.");
        }
    }

    private void ValidateRelativePath(string path, string label)
    {
        if (!ArchiveRelativePath.TryParse(path, int.MaxValue, int.MaxValue, out _, out var error))
        {
            log.LogError($"Sunder {label} path '{path}' is unsafe: {error}.");
        }
    }
}
