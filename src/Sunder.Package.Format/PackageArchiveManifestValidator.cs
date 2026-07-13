using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

public enum SunderPackageManifestLayout
{
    Archive,
    Activation,
}

public static class SunderPackageManifestValidator
{
    public static IReadOnlyList<string> Validate(
        SunderPackageManifest? manifest,
        string rootPath,
        SunderPackageManifestLayout layout = SunderPackageManifestLayout.Archive)
    {
        var errors = new List<string>();
        Validate(manifest, rootPath, errors, layout);
        return errors;
    }

    public static void Validate(
        SunderPackageManifest? manifest,
        string rootPath,
        ICollection<string> errors,
        SunderPackageManifestLayout layout = SunderPackageManifestLayout.Archive)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is empty or invalid.");
            return;
        }
        if (manifest.ManifestVersion != SunderPackageFormat.CurrentManifestVersion)
        {
            errors.Add($"Package manifest must declare manifestVersion {SunderPackageFormat.CurrentManifestVersion}.");
        }
        if (!PackageId.TryParse(manifest.Id, out _))
        {
            errors.Add($"Package id '{manifest.Id}' must use lowercase dot-separated ASCII identifiers.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? rootPath}' is missing name.");
        }
        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Package version '{manifest.Version}' must be strict SemVer 2.0.");
        }

        PackageAssetValidator.ValidateEntryAssembly(manifest, rootPath, errors, layout);
        PackageAssetValidator.ValidateIcon(manifest, rootPath, errors, layout);
        PackageDependencyValidator.Validate(manifest.DependsOn, errors);

        if (manifest.SdkApiVersion != SunderPackageFormat.CurrentSdkApiVersion)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? rootPath}' must declare sdkApiVersion {SunderPackageFormat.CurrentSdkApiVersion}.");
        }
        if (!SemanticVersion.TryParse(manifest.SdkPackageVersion, out _))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? rootPath}' must declare a strict SemVer 2.0 sdkPackageVersion.");
        }
        ValidateCapabilities(manifest, rootPath, errors);
    }

    private static void ValidateCapabilities(SunderPackageManifest manifest, string stagingPath, ICollection<string> errors)
    {
        var capabilities = manifest.RequiredSdkCapabilities;
        if (capabilities is null || capabilities.Count == 0)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' must declare requiredSdkCapabilities.");
            return;
        }

        var seenCapabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (!SunderPackageFormat.IsSdkCapabilityId(capability))
            {
                errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' declares invalid SDK capability '{capability}'.");
            }
            else if (!seenCapabilities.Add(capability))
            {
                errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' declares SDK capability '{capability}' more than once.");
            }
        }
    }
}
