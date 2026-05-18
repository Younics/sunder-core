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

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add($"Package manifest at '{shadowFolder}' is missing 'id'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' is missing 'name'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' is missing 'version'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? shadowFolder}' is missing 'entryAssembly'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            var entryAssemblyPath = Path.Combine(shadowFolder, "lib", manifest.EntryAssembly);
            if (!File.Exists(entryAssemblyPath))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' is missing entry assembly '{manifest.EntryAssembly}' under lib/.");
            }
        }

        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency.PackageId))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' has a dependency without a 'packageId'.");
            }

            if (string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                errors.Add($"Package '{manifest.Id ?? shadowFolder}' dependency '{dependency.PackageId ?? "unknown"}' is missing 'versionRange'.");
            }
        }

        return errors;
    }
}
