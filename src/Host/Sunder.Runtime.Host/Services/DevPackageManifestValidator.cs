namespace Sunder.Runtime.Host.Services;

internal static class DevPackageManifestValidator
{
    public static IReadOnlyList<string> Validate(DevPackageManifest? manifest, string shadowFolder)
    {
        var errors = new List<string>();

        if (manifest is null)
        {
            errors.Add($"Dev package manifest at '{shadowFolder}' is empty or invalid.");
            return errors;
        }

        if (manifest.ManifestVersion != 1)
        {
            errors.Add($"Dev package manifest for '{manifest.Id ?? shadowFolder}' must declare 'manifestVersion' 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add($"Dev package manifest at '{shadowFolder}' is missing 'id'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Dev package manifest for '{manifest.Id ?? shadowFolder}' is missing 'name'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add($"Dev package manifest for '{manifest.Id ?? shadowFolder}' is missing 'version'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add($"Dev package manifest for '{manifest.Id ?? shadowFolder}' is missing 'entryAssembly'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            var entryAssemblyPath = Path.Combine(shadowFolder, "lib", manifest.EntryAssembly);
            if (!File.Exists(entryAssemblyPath))
            {
                errors.Add($"Dev package '{manifest.Id ?? shadowFolder}' is missing entry assembly '{manifest.EntryAssembly}' under lib/.");
            }
        }

        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency.PackageId))
            {
                errors.Add($"Dev package '{manifest.Id ?? shadowFolder}' has a dependency without a 'packageId'.");
            }

            if (string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                errors.Add($"Dev package '{manifest.Id ?? shadowFolder}' dependency '{dependency.PackageId ?? "unknown"}' is missing 'versionRange'.");
            }
        }

        return errors;
    }
}
