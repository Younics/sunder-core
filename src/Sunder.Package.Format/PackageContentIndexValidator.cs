namespace Sunder.Package.Format;

internal static class PackageContentIndexValidator
{
    public static async Task ValidateAsync(
        SunderPackageContentIndex? contentIndex,
        string stagingPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (contentIndex is null)
        {
            errors.Add("Package content index is empty or invalid.");
            return;
        }
        if (contentIndex.SchemaVersion != SunderPackageFormat.CurrentContentIndexVersion)
        {
            errors.Add($"Package content index must declare schemaVersion {SunderPackageFormat.CurrentContentIndexVersion}.");
        }
        if (contentIndex.Files is null)
        {
            errors.Add("Package content index is missing files.");
            return;
        }

        var indexedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in contentIndex.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                errors.Add("Package content index contains a null file entry.");
                continue;
            }
            if (!PackageArchivePathValidator.TryParse(entry.Path, "content-index path", errors, out var path))
            {
                continue;
            }
            var normalizedPath = path.ToString();
            if (!SunderPackageFormat.IsAllowedArchivePath(normalizedPath))
            {
                errors.Add($"Package content index path '{normalizedPath}' is outside canonical manifest/, payload/lib/, and payload/assets/ roots.");
            }
            if (indexedPaths.TryGetValue(normalizedPath, out var existing))
            {
                var collision = string.Equals(existing, normalizedPath, StringComparison.Ordinal) ? "duplicate" : "case-colliding";
                errors.Add($"Package content index contains {collision} path '{normalizedPath}'.");
                continue;
            }
            indexedPaths.Add(normalizedPath, normalizedPath);
            if (SunderPackageFormat.IsContentIndexPath(normalizedPath))
            {
                errors.Add($"Package content index must not index itself at '{normalizedPath}'.");
                continue;
            }
            if (entry.Size < 0)
            {
                errors.Add($"Package content index path '{normalizedPath}' has invalid size {entry.Size}.");
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(entry.Sha256))
            {
                errors.Add($"Package content index path '{normalizedPath}' must declare a lowercase SHA-256 hash.");
            }
            var expectedRole = SunderPackageFormat.GetContentRole(normalizedPath);
            if (expectedRole is null)
            {
                if (string.IsNullOrWhiteSpace(entry.Role))
                {
                    errors.Add($"Package content index path '{normalizedPath}' must declare role.");
                }
            }
            else if (!string.Equals(entry.Role, expectedRole, StringComparison.Ordinal))
            {
                errors.Add($"Package content index path '{normalizedPath}' must declare role '{expectedRole}', not '{entry.Role}'.");
            }
            await PackageContentSignatureValidator.ValidateAsync(
                entry, stagingPath, path, normalizedPath, errors, cancellationToken);
        }

        foreach (var actualFile in SunderArchive.EnumerateFiles(stagingPath))
        {
            var actualPath = actualFile.Path.ToString();
            if (!SunderPackageFormat.IsAllowedArchivePath(actualPath))
            {
                errors.Add($"Package archive contains file outside canonical roots: '{actualPath}'.");
            }
            if (!SunderPackageFormat.IsContentIndexPath(actualPath)
                && !indexedPaths.ContainsKey(actualPath))
            {
                errors.Add($"Package archive contains unindexed file '{actualPath}'.");
            }
        }
    }
}
