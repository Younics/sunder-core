using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

public static class SunderPackageManifestValidator
{
    public static IReadOnlyList<string> Validate(
        SunderPackageManifest? manifest,
        string rootPath)
    {
        var errors = new List<string>();
        Validate(manifest, rootPath, errors);
        return errors;
    }

    public static void Validate(
        SunderPackageManifest? manifest,
        string rootPath,
        ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is empty or invalid.");
            return;
        }
        if (manifest.ArchiveFormatVersion != SunderPackageFormat.CurrentArchiveFormatVersion)
        {
            errors.Add($"Package manifest must declare archiveFormatVersion {SunderPackageFormat.CurrentArchiveFormatVersion}.");
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
        else if (manifest.Name.Length > 256)
        {
            errors.Add($"Package name for '{manifest.Id ?? rootPath}' must not exceed 256 characters.");
        }
        if (manifest.Summary is not null
            && (string.IsNullOrWhiteSpace(manifest.Summary) || manifest.Summary.Length > 2048))
        {
            errors.Add($"Package summary for '{manifest.Id ?? rootPath}' must be non-empty and at most 2048 characters when declared.");
        }
        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Package version '{manifest.Version}' must be strict SemVer 2.0.");
        }

        var files = Directory.Exists(rootPath)
            ? SunderArchive.EnumerateFiles(rootPath)
            : [];
        var physicalFiles = files.ToDictionary(
            static file => file.Path.ToString(),
            static file => file.FullPath,
            StringComparer.Ordinal);
        PackageTargetValidator.Validate(manifest, files.Select(static file => file.Path), errors);
        PackageAssetValidator.ValidateIcon(manifest, physicalFiles, errors);
        PackageContractValidator.Validate(manifest, physicalFiles, errors);
        PackageDependencyValidator.Validate(manifest.DependsOn, errors);
    }
}
