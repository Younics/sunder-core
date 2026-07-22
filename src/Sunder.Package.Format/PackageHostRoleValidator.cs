namespace Sunder.Package.Format;

internal static class PackageHostRoleValidator
{
    public static void Validate(
        SunderPackageManifest manifest,
        string rootPath,
        ICollection<string> errors,
        SunderPackageManifestLayout layout)
    {
        if (!PackageHostRoleMetadata.TryParseManifestRoles(manifest.HostRoles, out var declared, out var roleError))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? rootPath}' {roleError}.");
            return;
        }
        if (!ArchiveRelativePath.TryParse(manifest.EntryAssembly, int.MaxValue, int.MaxValue, out var entryAssembly, out _))
        {
            return;
        }

        var libraryRoot = layout == SunderPackageManifestLayout.Archive
            ? SunderPackageFormat.LibraryPayloadRoot
            : "lib/";
        var assemblyPath = SunderArchive.ResolveFile(rootPath, ArchiveRelativePath.Parse(libraryRoot + entryAssembly));
        if (!File.Exists(assemblyPath))
        {
            return;
        }

        try
        {
            var shape = PackageModuleShapeReader.Read(assemblyPath);
            foreach (var shapeError in shape.Validate())
            {
                errors.Add($"Package entry assembly '{manifest.EntryAssembly}' has an invalid module shape: {shapeError}");
            }
            if (shape.Roles != declared)
            {
                errors.Add(
                    $"Package manifest for '{manifest.Id ?? rootPath}' declares hostRoles [{string.Join(", ", manifest.HostRoles!)}], "
                    + $"but entry assembly metadata requires [{string.Join(", ", PackageHostRoleMetadata.ToManifestRoles(shape.Roles))}].");
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"Package entry assembly '{manifest.EntryAssembly}' could not be inspected for host roles: {exception.Message}");
        }
    }
}
