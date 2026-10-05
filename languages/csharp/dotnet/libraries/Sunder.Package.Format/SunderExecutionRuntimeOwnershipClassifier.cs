namespace Sunder.Package.Format;

public sealed record SunderExecutionRuntimeOwnershipInventory(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi,
    string ContentIdentity,
    IReadOnlyList<SunderExecutionRuntimeContentRecord> Content);

public sealed record SunderExecutionRuntimeOwnershipMatch(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi,
    string ContentIdentity,
    string RuntimePath,
    bool RelativePathMatch,
    bool FileNameMatch,
    bool Sha256Match);

public sealed record SunderPackageRuntimeOwnedFile(
    string PackagePath,
    string Sha256,
    long Size,
    IReadOnlyList<SunderExecutionRuntimeOwnershipMatch> Matches);

public static class SunderExecutionRuntimeOwnershipClassifier
{
    private const string PayloadRoot = "payload/";

    public static bool IsRuntimeOwnedContentPath(string? path)
    {
        if (path is null
            || !path.StartsWith(PayloadRoot, StringComparison.Ordinal)
            || path.StartsWith("payload/provenance/", StringComparison.Ordinal)
            || path.StartsWith("payload/licenses/", StringComparison.Ordinal))
        {
            return false;
        }
        var fileName = FileName(path);
        return !fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               && !fileName.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase)
               && !fileName.Equals("NOTICE", StringComparison.OrdinalIgnoreCase)
               && !fileName.StartsWith("NOTICE.", StringComparison.OrdinalIgnoreCase)
               && !fileName.StartsWith("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase)
               && !fileName.StartsWith("THIRDPARTYNOTICES", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<SunderPackageRuntimeOwnedFile> Classify(
        IEnumerable<SunderPackageContentIndexEntry> packageFiles,
        IEnumerable<SunderExecutionRuntimeOwnershipInventory> runtimeInventories)
    {
        ArgumentNullException.ThrowIfNull(packageFiles);
        ArgumentNullException.ThrowIfNull(runtimeInventories);

        var runtimeFiles = runtimeInventories
            .SelectMany(ValidateInventory)
            .ToArray();
        var result = new List<SunderPackageRuntimeOwnedFile>();
        foreach (var packageFile in packageFiles)
        {
            ArgumentNullException.ThrowIfNull(packageFile);
            var packagePath = ValidatePackagePath(packageFile.Path);
            var packageSha256 = ValidateSha256(packageFile.Sha256, nameof(packageFiles));
            if (packageFile.Size < 0)
            {
                throw new ArgumentException("Package file sizes cannot be negative.", nameof(packageFiles));
            }

            var packageRelativePaths = PackageRelativePaths(packagePath);
            var packageFileName = FileName(packagePath);
            var matches = runtimeFiles
                .Select(runtimeFile =>
                {
                    var relativePathMatch = packageRelativePaths.Overlaps(runtimeFile.RelativePaths);
                    var fileNameMatch = string.Equals(packageFileName, runtimeFile.FileName, StringComparison.OrdinalIgnoreCase);
                    var sha256Match = string.Equals(packageSha256, runtimeFile.Record.Sha256, StringComparison.Ordinal);
                    return relativePathMatch || fileNameMatch || sha256Match
                        ? new SunderExecutionRuntimeOwnershipMatch(
                            runtimeFile.Inventory.RuntimeId,
                            runtimeFile.Inventory.Version,
                            runtimeFile.Inventory.PackagingRevision,
                            runtimeFile.Inventory.Rid,
                            runtimeFile.Inventory.ModuleAbi,
                            runtimeFile.Inventory.ContentIdentity,
                            runtimeFile.Record.Path!,
                            relativePathMatch,
                            fileNameMatch,
                            sha256Match)
                        : null;
                })
                .Where(static match => match is not null)
                .Select(static match => match!)
                .OrderBy(static match => match.RuntimeId, StringComparer.Ordinal)
                .ThenBy(static match => match.Version, StringComparer.Ordinal)
                .ThenBy(static match => match.PackagingRevision)
                .ThenBy(static match => match.Rid, StringComparer.Ordinal)
                .ThenBy(static match => match.RuntimePath, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length > 0)
            {
                result.Add(new SunderPackageRuntimeOwnedFile(
                    packagePath,
                    packageSha256,
                    packageFile.Size,
                    matches));
            }
        }
        return result.AsReadOnly();
    }

    private static IEnumerable<RuntimeFile> ValidateInventory(
        SunderExecutionRuntimeOwnershipInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.RuntimeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.Rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ModuleAbi);
        ValidateSha256(inventory.ContentIdentity, nameof(inventory.ContentIdentity));
        ArgumentNullException.ThrowIfNull(inventory.Content);

        foreach (var record in inventory.Content)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Path is not { } path || !IsRuntimeOwnedContentPath(path))
            {
                continue;
            }
            if (!ArchiveRelativePath.TryParse(
                    path,
                    SunderExecutionRuntimeFormat.MaxArchivePathLength,
                    SunderExecutionRuntimeFormat.MaxArchivePathDepth,
                    out _,
                    out _))
            {
                throw new ArgumentException($"Execution-runtime inventory path '{path}' is invalid.", nameof(inventory));
            }
            ValidateSha256(record.Sha256, nameof(inventory));
            if (record.Size < 0)
            {
                throw new ArgumentException("Execution-runtime file sizes cannot be negative.", nameof(inventory));
            }
            var relativePaths = RuntimeRelativePaths(path);
            yield return new RuntimeFile(inventory, record, relativePaths, FileName(path));
        }
    }

    private static HashSet<string> RuntimeRelativePaths(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { path };
        if (path.StartsWith("payload/runtime/", StringComparison.Ordinal))
        {
            result.Add(path["payload/runtime/".Length..]);
        }
        else if (path.StartsWith(PayloadRoot, StringComparison.Ordinal))
        {
            result.Add(path[PayloadRoot.Length..]);
        }
        return result;
    }

    private static HashSet<string> PackageRelativePaths(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { path };
        if (path.StartsWith(SunderPackageFormat.SharedPayloadRoot, StringComparison.Ordinal))
        {
            result.Add(path[SunderPackageFormat.SharedPayloadRoot.Length..]);
            return result;
        }

        foreach (var root in new[] { "payload/runtime/", "payload/app/" })
        {
            if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
            var ridSeparator = path.IndexOf('/', root.Length);
            if (ridSeparator >= 0 && ridSeparator + 1 < path.Length)
            {
                result.Add(path[(ridSeparator + 1)..]);
            }
            break;
        }
        return result;
    }

    private static string ValidatePackagePath(string? path)
    {
        if (!ArchiveRelativePath.TryParse(
                path,
                SunderPackageFormat.MaxArchivePathLength,
                SunderPackageFormat.MaxArchivePathDepth,
                out var parsed,
                out var error))
        {
            throw new ArgumentException($"Package inventory path is invalid: {error}", nameof(path));
        }
        return parsed.ToString();
    }

    private static string ValidateSha256(string? value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lower-case SHA-256 digest is required.", parameterName);
        }
        return value;
    }

    private static string FileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private sealed record RuntimeFile(
        SunderExecutionRuntimeOwnershipInventory Inventory,
        SunderExecutionRuntimeContentRecord Record,
        IReadOnlySet<string> RelativePaths,
        string FileName);
}
