namespace Sunder.Package.Format;

internal static class PackageAssetValidator
{
    public static void ValidateIcon(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        if (manifest.Icon is null)
        {
            return;
        }
        if (!PackageArchivePathValidator.TryParse(manifest.Icon, "icon", errors, out var icon, required: true))
        {
            return;
        }

        var physicalPath = SunderPackageFormat.SharedPayloadRoot + icon;
        if (!physicalFiles.TryGetValue(physicalPath, out var filePath))
        {
            errors.Add($"Package icon '{manifest.Icon}' must exist physically at '{physicalPath}'.");
            return;
        }

        var length = new FileInfo(filePath).Length;
        if (length <= 0 || length > SunderPackageFormat.MaxIconBytes)
        {
            errors.Add($"Package icon '{manifest.Icon}' must be non-empty and 1 MiB or smaller.");
            return;
        }
        if (!ImageFileInspector.TryRead(filePath, out var image, out var error))
        {
            errors.Add($"Package icon '{manifest.Icon}' is invalid: {error}.");
        }
        else if (!ImageFileInspector.ExtensionMatches(filePath, image.ContentType))
        {
            errors.Add($"Package icon '{manifest.Icon}' file extension does not match its '{image.ContentType}' signature.");
        }
        else if (image.Width > SunderPackageFormat.MaxIconDimension || image.Height > SunderPackageFormat.MaxIconDimension)
        {
            errors.Add($"Package icon '{manifest.Icon}' dimensions must not exceed {SunderPackageFormat.MaxIconDimension}x{SunderPackageFormat.MaxIconDimension} pixels.");
        }
    }
}
