using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.PackageManagement;

public static class SunderPackageArchiveInspector
{
    private static readonly Regex PackageIdRegex = new("^[a-z0-9]+(\\.[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemVerRegex = new("^\\d+\\.\\d+\\.\\d+([-.+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<SunderPackageArchiveValidationResult> ExtractAndValidateAsync(
        string packagePath,
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        ExtractArchive(packagePath, stagingPath);
        return await ValidateExtractedPackageAsync(stagingPath, cancellationToken);
    }

    public static void ExtractArchive(string packagePath, string stagingPath)
    {
        Directory.CreateDirectory(stagingPath);
        var stagingRoot = Path.GetFullPath(stagingPath);
        if (!stagingRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            stagingRoot += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(stagingPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Package archive contains unsafe path '{entry.FullName}'.");
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

    public static async Task<SunderPackageArchiveValidationResult> ValidateExtractedPackageAsync(
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestPath = Path.Combine(stagingPath, "manifest", "sunder-package.json");
        var contentIndexPath = Path.Combine(stagingPath, "manifest", "content-index.json");

        if (!File.Exists(manifestPath))
        {
            errors.Add("Package archive is missing manifest/sunder-package.json.");
        }

        if (!File.Exists(contentIndexPath))
        {
            errors.Add("Package archive is missing manifest/content-index.json.");
        }

        if (errors.Count > 0)
        {
            return new SunderPackageArchiveValidationResult(null, warnings, errors);
        }

        SunderPackageManifest? manifest = null;
        SunderPackageContentIndex? contentIndex = null;
        try
        {
            manifest = JsonSerializer.Deserialize<SunderPackageManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions);
            contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(contentIndexPath, cancellationToken), JsonOptions);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse package metadata: {ex.Message}");
            return new SunderPackageArchiveValidationResult(null, warnings, errors);
        }

        ValidateManifest(manifest, stagingPath, errors);
        ValidateContentIndex(contentIndex, stagingPath, errors);
        return new SunderPackageArchiveValidationResult(errors.Count == 0 ? manifest : null, warnings, errors);
    }

    private static void ValidateManifest(SunderPackageManifest? manifest, string stagingPath, ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is empty or invalid.");
            return;
        }

        if (manifest.ManifestVersion != 1)
        {
            errors.Add("Package manifest must declare manifestVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || !PackageIdRegex.IsMatch(manifest.Id))
        {
            errors.Add($"Package id '{manifest.Id}' must use lowercase dot-separated ASCII identifiers.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' is missing name.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemVerRegex.IsMatch(manifest.Version))
        {
            errors.Add($"Package version '{manifest.Version}' must be SemVer-compatible.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' is missing entryAssembly.");
        }
        else
        {
            ValidateRelativePath(manifest.EntryAssembly, "entryAssembly", errors);
            if (!File.Exists(Path.Combine(stagingPath, "payload", "lib", manifest.EntryAssembly)))
            {
                errors.Add($"Package '{manifest.Id ?? stagingPath}' is missing entry assembly '{manifest.EntryAssembly}' under payload/lib/.");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            ValidateRelativePath(manifest.Icon, "icon", errors);
            var artifactIconPath = manifest.Icon.Replace('\\', '/').StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(stagingPath, "payload", manifest.Icon.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(stagingPath, manifest.Icon.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(artifactIconPath))
            {
                errors.Add($"Package icon '{manifest.Icon}' was not found in the package artifact.");
            }
        }

        var seenDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency.PackageId) || !PackageIdRegex.IsMatch(dependency.PackageId))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenDependencies.Add(dependency.PackageId))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' is missing versionRange.");
            }
        }

        if (manifest.SdkApiVersion is <= 0)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' has an invalid sdkApiVersion.");
        }

        foreach (var capability in manifest.RequiredSdkCapabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' declares an empty requiredSdkCapabilities entry.");
            }
        }
    }

    private static void ValidateContentIndex(SunderPackageContentIndex? contentIndex, string stagingPath, ICollection<string> errors)
    {
        if (contentIndex is null)
        {
            errors.Add("Package content index is empty or invalid.");
            return;
        }

        if (contentIndex.SchemaVersion != 1)
        {
            errors.Add("Package content index must declare schemaVersion 1.");
        }

        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in contentIndex.Files)
        {
            var normalizedPath = entry.Path.Replace('\\', '/');
            ValidateRelativePath(normalizedPath, "content-index path", errors);
            if (!indexedPaths.Add(normalizedPath))
            {
                errors.Add($"Package content index contains duplicate path '{normalizedPath}'.");
                continue;
            }

            var filePath = Path.Combine(stagingPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                errors.Add($"Package content index references missing file '{normalizedPath}'.");
                continue;
            }

            var info = new FileInfo(filePath);
            if (info.Length != entry.Size)
            {
                errors.Add($"Package file '{normalizedPath}' size mismatch.");
            }

            using var stream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Package file '{normalizedPath}' SHA-256 mismatch.");
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
                errors.Add($"Package archive contains unindexed file '{actualFile}'.");
            }
        }
    }

    private static void ValidateRelativePath(string path, string label, ICollection<string> errors)
    {
        if (Path.IsPathRooted(path))
        {
            errors.Add($"Package {label} '{path}' must be relative.");
            return;
        }

        var normalizedSegments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedSegments.Any(static segment => segment == ".."))
        {
            errors.Add($"Package {label} '{path}' must not contain parent directory traversal.");
        }
    }
}
