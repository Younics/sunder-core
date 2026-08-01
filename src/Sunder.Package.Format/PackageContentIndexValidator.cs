namespace Sunder.Package.Format;

internal static class PackageContentIndexValidator
{
    public static async Task ValidateAsync(
        SunderPackageContentIndex? contentIndex,
        string stagingPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var actualFiles = SunderArchive.EnumerateFiles(stagingPath);
        var actualFilesByPath = actualFiles.ToDictionary(
            static file => file.Path.ToString(),
            static file => file.FullPath,
            StringComparer.Ordinal);
        var actualContentIsBounded = ValidateActualContentBounds(actualFiles, errors);
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
        if (contentIndex.Files.Count > SunderPackageFormat.MaxContentIndexEntries)
        {
            errors.Add($"Package content index contains {contentIndex.Files.Count} files; the limit is {SunderPackageFormat.MaxContentIndexEntries}.");
        }

        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (var entry in contentIndex.Files.Take(SunderPackageFormat.MaxContentIndexEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                errors.Add("Package content index contains a null file entry.");
                continue;
            }
            if (!PackageArchivePathValidator.TryParse(
                    entry.Path,
                    "content-index path",
                    errors,
                    out var path,
                    required: true,
                    maxLength: SunderPackageFormat.MaxArchivePathLength,
                    maxDepth: SunderPackageFormat.MaxArchivePathDepth))
            {
                continue;
            }
            var normalizedPath = path.ToString();
            if (!SunderPackageFormat.IsAllowedArchivePath(normalizedPath))
            {
                errors.Add($"Package content index path '{normalizedPath}' is outside canonical manifest and payload layer roots.");
            }
            if (!indexedPaths.Add(normalizedPath))
            {
                errors.Add($"Package content index contains duplicate path '{normalizedPath}'.");
                continue;
            }
            if (portablePaths.TryGetValue(normalizedPath, out var existing))
            {
                errors.Add($"Package content index paths '{existing}' and '{normalizedPath}' differ only by case.");
                continue;
            }
            portablePaths.Add(normalizedPath, normalizedPath);
            if (SunderPackageFormat.IsContentIndexPath(normalizedPath))
            {
                errors.Add($"Package content index must not index itself at '{normalizedPath}'.");
                continue;
            }
            if (entry.Size < 0)
            {
                errors.Add($"Package content index path '{normalizedPath}' has invalid size {entry.Size}.");
            }
            else if (entry.Size > SunderArchiveExtractionOptions.Default.MaxEntryUncompressedBytes)
            {
                errors.Add($"Package content index path '{normalizedPath}' exceeds the per-entry size limit.");
            }
            else if (entry.Size > SunderArchiveExtractionOptions.Default.MaxTotalUncompressedBytes - declaredBytes)
            {
                errors.Add("Package content index exceeds the total uncompressed size limit.");
            }
            else
            {
                declaredBytes += entry.Size;
            }
            if (!PackageContentSignatureValidator.IsLowercaseSha256(entry.Sha256))
            {
                errors.Add($"Package content index path '{normalizedPath}' must declare a lowercase SHA-256 hash.");
            }
            actualFilesByPath.TryGetValue(normalizedPath, out var filePath);
            var validateHash = actualContentIsBounded
                               && entry.Size >= 0
                               && entry.Size <= SunderArchiveExtractionOptions.Default.MaxEntryUncompressedBytes
                               && PackageContentSignatureValidator.IsLowercaseSha256(entry.Sha256);
            await PackageContentSignatureValidator.ValidateAsync(
                entry, filePath, normalizedPath, errors, validateHash, cancellationToken);
        }

        foreach (var actualFile in actualFiles)
        {
            var actualPath = actualFile.Path.ToString();
            if (!SunderPackageFormat.IsAllowedArchivePath(actualPath))
            {
                errors.Add($"Package archive contains file outside canonical roots: '{actualPath}'.");
            }
            if (!SunderPackageFormat.IsContentIndexPath(actualPath)
                && !indexedPaths.Contains(actualPath))
            {
                errors.Add($"Package archive contains unindexed file '{actualPath}'.");
            }
        }

        foreach (var directory in SunderArchiveFileSystem.EnumerateDirectories(stagingPath))
        {
            if (!SunderPackageFormat.IsAllowedArchiveDirectoryPath(directory.ToString()))
            {
                errors.Add($"Package archive contains directory outside canonical roots: '{directory}'.");
            }
        }
    }

    private static bool ValidateActualContentBounds(
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles,
        ICollection<string> errors)
    {
        var options = SunderArchiveExtractionOptions.Default;
        var bounded = true;
        if (actualFiles.Count > options.MaxEntries)
        {
            errors.Add($"Package staging directory contains {actualFiles.Count} files; the limit is {options.MaxEntries}.");
            bounded = false;
        }

        long totalBytes = 0;
        var totalErrorAdded = false;
        foreach (var file in actualFiles)
        {
            var length = new FileInfo(file.FullPath).Length;
            if (length > options.MaxEntryUncompressedBytes)
            {
                errors.Add($"Package file '{file.Path}' exceeds the per-entry size limit.");
                bounded = false;
            }
            if (length > options.MaxTotalUncompressedBytes - totalBytes)
            {
                if (!totalErrorAdded)
                {
                    errors.Add("Package staging directory exceeds the total uncompressed size limit.");
                    totalErrorAdded = true;
                }
                bounded = false;
            }
            else
            {
                totalBytes += length;
            }
        }

        return bounded;
    }
}
