using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimePackageManifestValidator
{
    public static IReadOnlyList<string> Validate(RuntimePackageManifest? manifest, string shadowFolder)
    {
        var errors = new List<string>();

        if (manifest is null)
        {
            errors.Add($"Package manifest at '{shadowFolder}' is empty or invalid.");
            return errors;
        }

        if (manifest.ManifestVersion != 1)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' must declare 'manifestVersion' 1.");
        }

        errors.AddRange(SunderSdkCompatibilityProfile.Validate(manifest));

        if (!PackageId.TryParse(manifest.Id, out _))
        {
            errors.Add($"Package manifest at '{shadowFolder}' has invalid 'id' '{manifest.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' is missing 'name'.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' must declare a strict SemVer 2.0 'version'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' is missing 'entryAssembly'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            if (!ArchiveRelativePath.TryParse(manifest.EntryAssembly, int.MaxValue, int.MaxValue, out var entryAssembly, out var pathError))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' entryAssembly '{manifest.EntryAssembly}' is unsafe: {pathError}.");
            }
            else if (!File.Exists(entryAssembly.ToPlatformPath(Path.Combine(shadowFolder, "lib"))))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' is missing entry assembly '{manifest.EntryAssembly}' under lib/.");
            }
        }

        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (!PackageId.TryParse(dependency.PackageId, out _))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' has an invalid dependency packageId '{dependency.PackageId}'.");
            }

            if (!PackageVersionRange.TryParse(dependency.VersionRange, out _))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' dependency '{dependency.PackageId ?? "unknown"}' has unsupported versionRange '{dependency.VersionRange}'.");
            }
        }

        if (!SemanticVersion.TryParse(manifest.SdkPackageVersion, out _))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' must declare a strict SemVer 2.0 'sdkPackageVersion'.");
        }

        var capabilities = manifest.RequiredSdkCapabilities;
        if (capabilities is null || capabilities.Count == 0)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' must declare 'requiredSdkCapabilities'.");
        }
        else
        {
            var seenCapabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in capabilities)
            {
                if (!SunderPackageFormat.IsSdkCapabilityId(capability))
                {
                    errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' declares invalid SDK capability '{capability}'.");
                }
                else if (!seenCapabilities.Add(capability))
                {
                    errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' declares SDK capability '{capability}' more than once.");
                }
            }
        }

        return errors;
    }
}
