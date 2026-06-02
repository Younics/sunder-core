using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.PackageManagement;

public static class SunderStackArchiveInspector
{
    private static readonly Regex PackageIdRegex = new("^[a-z0-9]+(\\.[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StackIdRegex = new("^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FragmentIdRegex = new("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemVerRegex = new("^\\d+\\.\\d+\\.\\d+([-.+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<SunderStackArchiveValidationResult> ExtractAndValidateAsync(
        string stackPath,
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        ExtractArchive(stackPath, stagingPath);
        return await ValidateExtractedStackAsync(stagingPath, cancellationToken);
    }

    public static void ExtractArchive(string stackPath, string stagingPath)
    {
        Directory.CreateDirectory(stagingPath);
        var stagingRoot = Path.GetFullPath(stagingPath);
        if (!stagingRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            stagingRoot += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(stackPath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(stagingPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Stack archive contains unsafe path '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    public static async Task<SunderStackArchiveValidationResult> ValidateExtractedStackAsync(
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestPath = Path.Combine(stagingPath, "manifest", "sunder-stack.json");
        var contentIndexPath = Path.Combine(stagingPath, "manifest", "content-index.json");

        if (!File.Exists(manifestPath))
        {
            errors.Add("Stack archive is missing manifest/sunder-stack.json.");
        }

        if (!File.Exists(contentIndexPath))
        {
            errors.Add("Stack archive is missing manifest/content-index.json.");
        }

        if (errors.Count > 0)
        {
            return new SunderStackArchiveValidationResult(null, warnings, errors);
        }

        SunderStackManifest? manifest = null;
        SunderStackContentIndex? contentIndex = null;
        try
        {
            manifest = JsonSerializer.Deserialize<SunderStackManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions);
            contentIndex = JsonSerializer.Deserialize<SunderStackContentIndex>(await File.ReadAllTextAsync(contentIndexPath, cancellationToken), JsonOptions);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse Stack metadata: {ex.Message}");
            return new SunderStackArchiveValidationResult(null, warnings, errors);
        }

        ValidateManifest(manifest, stagingPath, warnings, errors);
        ValidateContentIndex(contentIndex, stagingPath, errors);
        foreach (var finding in SunderStackSecretScanner.ScanExtractedStack(stagingPath))
        {
            errors.Add(finding);
        }

        return new SunderStackArchiveValidationResult(errors.Count == 0 ? manifest : null, warnings, errors);
    }

    private static void ValidateManifest(
        SunderStackManifest? manifest,
        string stagingPath,
        ICollection<string> warnings,
        ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Stack manifest is empty or invalid.");
            return;
        }

        if (manifest.SchemaVersion != 1)
        {
            errors.Add("Stack manifest must declare schemaVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.StackId) || !StackIdRegex.IsMatch(manifest.StackId))
        {
            errors.Add($"Stack id '{manifest.StackId}' must use lowercase ASCII identifiers separated by dot or dash.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Stack manifest for '{manifest.StackId ?? stagingPath}' is missing name.");
        }

        var packages = manifest.Packages ?? [];
        var fragments = manifest.Fragments ?? [];
        if (packages.Count == 0 && fragments.Count == 0)
        {
            errors.Add("Stack manifest must include at least one package requirement or fragment.");
        }

        ValidatePackageRequirements(packages, errors);
        ValidateFragments(fragments, stagingPath, warnings, errors);
        ValidateRequiredInputs(manifest.RequiredInputs ?? [], "Stack", errors);

        if (manifest.Safety?.ContainsSecrets == true)
        {
            warnings.Add($"Stack '{manifest.StackId ?? stagingPath}' declares that it contains raw secrets.");
        }
    }

    private static void ValidatePackageRequirements(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        ICollection<string> errors)
    {
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.PackageId) || !PackageIdRegex.IsMatch(package.PackageId))
            {
                errors.Add($"Stack package id '{package.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenPackages.Add(package.PackageId))
            {
                errors.Add($"Stack package id '{package.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(package.InstallTag) || !TagRegex.IsMatch(package.InstallTag))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' must declare a valid installTag.");
            }

            if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion) && !SemVerRegex.IsMatch(package.CreatedWithVersion))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' has invalid createdWithVersion '{package.CreatedWithVersion}'.");
            }

            if (!string.IsNullOrWhiteSpace(package.MinimumVersion) && !SemVerRegex.IsMatch(package.MinimumVersion))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' has invalid minimumVersion '{package.MinimumVersion}'.");
            }

            if (package.Required is null)
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' must declare required.");
            }
        }
    }

    private static void ValidateFragments(
        IReadOnlyList<SunderStackFragmentManifest> fragments,
        string stagingPath,
        ICollection<string> warnings,
        ICollection<string> errors)
    {
        var seenFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in fragments)
        {
            var fragmentId = fragment.FragmentId ?? "unknown";
            if (string.IsNullOrWhiteSpace(fragment.FragmentId) || !FragmentIdRegex.IsMatch(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' must use lowercase ASCII identifiers separated by dot, dash, or underscore.");
            }
            else if (!seenFragments.Add(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(fragment.OwnerPackageId) || !PackageIdRegex.IsMatch(fragment.OwnerPackageId))
            {
                errors.Add($"Stack fragment '{fragmentId}' ownerPackageId '{fragment.OwnerPackageId}' must use lowercase dot-separated ASCII identifiers.");
            }

            if (string.IsNullOrWhiteSpace(fragment.ContributorId))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing contributorId.");
            }

            if (string.IsNullOrWhiteSpace(fragment.SchemaId))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing schemaId.");
            }

            if (fragment.SchemaVersion is null or <= 0)
            {
                errors.Add($"Stack fragment '{fragmentId}' must declare a positive schemaVersion.");
            }

            if (string.IsNullOrWhiteSpace(fragment.DisplayName))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing displayName.");
            }

            if (string.IsNullOrWhiteSpace(fragment.PayloadPath))
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing payloadPath.");
            }
            else
            {
                var payloadPath = fragment.PayloadPath.Replace('\\', '/');
                ValidateRelativePath(payloadPath, "fragment payloadPath", errors);
                if (!payloadPath.StartsWith("payload/", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' must be under payload/.");
                }
                else if (!File.Exists(Path.Combine(stagingPath, payloadPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' was not found in the Stack archive.");
                }
            }

            foreach (var packageId in fragment.RequiresPackages ?? [])
            {
                if (string.IsNullOrWhiteSpace(packageId) || !PackageIdRegex.IsMatch(packageId))
                {
                    errors.Add($"Stack fragment '{fragmentId}' requires invalid package id '{packageId}'.");
                }
            }

            if (fragment.Safety is null)
            {
                errors.Add($"Stack fragment '{fragmentId}' is missing safety metadata.");
            }
            else if (fragment.Safety.ContainsSecrets == true)
            {
                warnings.Add($"Stack fragment '{fragmentId}' declares that it contains raw secrets.");
            }

            ValidateRequiredInputs(fragment.RequiredInputs ?? [], $"Stack fragment '{fragmentId}'", errors);
        }
    }

    private static void ValidateRequiredInputs(
        IReadOnlyList<SunderStackRequiredInputManifest> requiredInputs,
        string label,
        ICollection<string> errors)
    {
        var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in requiredInputs)
        {
            if (string.IsNullOrWhiteSpace(input.InputId) || !FragmentIdRegex.IsMatch(input.InputId))
            {
                errors.Add($"{label} required input id '{input.InputId}' must use lowercase ASCII identifiers separated by dot, dash, or underscore.");
            }
            else if (!seenInputs.Add(input.InputId))
            {
                errors.Add($"{label} required input id '{input.InputId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(input.Kind))
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' is missing kind.");
            }

            if (string.IsNullOrWhiteSpace(input.Label))
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' is missing label.");
            }

            if (input.Required is null)
            {
                errors.Add($"{label} required input '{input.InputId ?? "unknown"}' must declare required.");
            }
        }
    }

    private static void ValidateContentIndex(SunderStackContentIndex? contentIndex, string stagingPath, ICollection<string> errors)
    {
        if (contentIndex is null)
        {
            errors.Add("Stack content index is empty or invalid.");
            return;
        }

        if (contentIndex.SchemaVersion != 1)
        {
            errors.Add("Stack content index must declare schemaVersion 1.");
        }

        if (contentIndex.Files is null)
        {
            errors.Add("Stack content index is missing files.");
            return;
        }

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in contentIndex.Files)
        {
            var normalizedPath = entry.Path.Replace('\\', '/');
            ValidateRelativePath(normalizedPath, "content-index path", errors);
            if (!indexedPaths.Add(normalizedPath))
            {
                errors.Add($"Stack content index contains duplicate path '{normalizedPath}'.");
                continue;
            }

            var filePath = Path.Combine(stagingPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                errors.Add($"Stack content index references missing file '{normalizedPath}'.");
                continue;
            }

            var info = new FileInfo(filePath);
            if (info.Length != entry.Size)
            {
                errors.Add($"Stack file '{normalizedPath}' size mismatch.");
            }

            using var stream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Stack file '{normalizedPath}' SHA-256 mismatch.");
            }
        }

        var actualFiles = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stagingPath, path).Replace('\\', '/'))
            .ToArray();
        foreach (var actualFile in actualFiles)
        {
            if (string.Equals(actualFile, "manifest/content-index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!indexedPaths.Contains(actualFile))
            {
                errors.Add($"Stack archive contains unindexed file '{actualFile}'.");
            }
        }
    }

    private static void ValidateRelativePath(string path, string label, ICollection<string> errors)
    {
        if (Path.IsPathRooted(path))
        {
            errors.Add($"Stack {label} '{path}' must be relative.");
            return;
        }

        var normalizedSegments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedSegments.Any(static segment => segment == ".."))
        {
            errors.Add($"Stack {label} '{path}' must not contain parent directory traversal.");
        }
    }
}
