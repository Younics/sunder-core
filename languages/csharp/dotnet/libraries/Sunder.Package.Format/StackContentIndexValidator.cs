using System.Security.Cryptography;

namespace Sunder.Package.Format;

internal static class StackContentIndexValidator
{
    public static async Task ValidateAsync(
        SunderStackContentIndex? contentIndex,
        string stagingPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (contentIndex is null)
        {
            errors.Add("Stack content index is empty or invalid.");
            return;
        }

        if (contentIndex.SchemaVersion != SunderStackFormat.CurrentContentIndexVersion)
        {
            errors.Add($"Stack content index must declare schemaVersion {SunderStackFormat.CurrentContentIndexVersion}.");
        }

        if (contentIndex.Files is null)
        {
            errors.Add("Stack content index is missing files.");
            return;
        }

        var indexedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in contentIndex.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                errors.Add("Stack content index contains a null file entry.");
                continue;
            }
            if (!StackArchivePathValidator.TryParse(
                    entry.Path,
                    "content-index path",
                    errors,
                    out var path,
                    required: true))
            {
                continue;
            }

            var normalizedPath = path.ToString();
            if (!SunderStackFormat.IsAllowedArchivePath(normalizedPath))
            {
                errors.Add($"Stack content index path '{normalizedPath}' is outside canonical manifest and payload roots.");
            }
            if (indexedPaths.TryGetValue(normalizedPath, out var existing))
            {
                var collision = string.Equals(existing, normalizedPath, StringComparison.Ordinal) ? "duplicate" : "case-colliding";
                errors.Add($"Stack content index contains {collision} path '{normalizedPath}'.");
                continue;
            }

            indexedPaths.Add(normalizedPath, normalizedPath);
            if (SunderStackFormat.IsContentIndexPath(normalizedPath))
            {
                errors.Add($"Stack content index must not index itself at '{normalizedPath}'.");
                continue;
            }

            if (entry.Size < 0)
            {
                errors.Add($"Stack content index path '{normalizedPath}' has invalid size {entry.Size}.");
            }

            if (!IsLowercaseSha256(entry.Sha256))
            {
                errors.Add($"Stack content index path '{normalizedPath}' must declare a lowercase SHA-256 hash.");
            }

            await ValidateFileAsync(entry, stagingPath, path, normalizedPath, errors, cancellationToken);
        }

        foreach (var actualFileEntry in SunderArchive.EnumerateFiles(stagingPath))
        {
            var actualPath = actualFileEntry.Path.ToString();
            if (!SunderStackFormat.IsAllowedArchivePath(actualPath))
            {
                errors.Add($"Stack archive contains file outside canonical roots: '{actualPath}'.");
            }
            if (!SunderStackFormat.IsContentIndexPath(actualPath)
                && !indexedPaths.ContainsKey(actualPath))
            {
                errors.Add($"Stack archive contains unindexed file '{actualPath}'.");
            }
        }
    }

    private static async Task ValidateFileAsync(
        SunderStackContentIndexEntry entry,
        string stagingPath,
        ArchiveRelativePath path,
        string normalizedPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var filePath = SunderArchive.ResolveFile(stagingPath, path);
        if (!File.Exists(filePath))
        {
            errors.Add($"Stack content index references missing file '{normalizedPath}'.");
            return;
        }

        var info = new FileInfo(filePath);
        if (info.Length != entry.Size)
        {
            errors.Add($"Stack file '{normalizedPath}' size mismatch.");
        }

        await using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
        {
            errors.Add($"Stack file '{normalizedPath}' SHA-256 mismatch.");
        }
    }

    private static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
