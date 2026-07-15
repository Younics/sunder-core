namespace Sunder.Package.Format;

internal static class PackageAssetValidator
{
    public static void ValidateEntryAssembly(
        SunderPackageManifest manifest,
        string rootPath,
        ICollection<string> errors,
        SunderPackageManifestLayout layout)
    {
        if (!PackageArchivePathValidator.TryParse(manifest.EntryAssembly, "entryAssembly", errors, out var entryAssembly))
        {
            if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            {
                errors.Add($"Package manifest for '{manifest.Id ?? rootPath}' is missing entryAssembly.");
            }
            return;
        }

        var libraryRoot = layout == SunderPackageManifestLayout.Archive
            ? SunderPackageFormat.LibraryPayloadRoot
            : "lib/";
        var libraryPath = ArchiveRelativePath.Parse(libraryRoot + entryAssembly);
        if (!File.Exists(SunderArchive.ResolveFile(rootPath, libraryPath)))
        {
            errors.Add($"Package '{manifest.Id ?? rootPath}' is missing entry assembly '{manifest.EntryAssembly}' under {libraryRoot}.");
        }
    }

    public static void ValidateIcon(
        SunderPackageManifest manifest,
        string rootPath,
        ICollection<string> errors,
        SunderPackageManifestLayout layout)
    {
        if (string.IsNullOrWhiteSpace(manifest.Icon)
            || !PackageArchivePathValidator.TryParse(manifest.Icon, "icon", errors, out var icon))
        {
            return;
        }

        var iconPath = layout == SunderPackageManifestLayout.Archive
            && icon.ToString().StartsWith("assets/", StringComparison.Ordinal)
            ? ArchiveRelativePath.Parse("payload/" + icon)
            : icon;
        var filePath = SunderArchive.ResolveFile(rootPath, iconPath);
        if (!File.Exists(filePath))
        {
            errors.Add($"Package icon '{manifest.Icon}' was not found in the package artifact.");
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
