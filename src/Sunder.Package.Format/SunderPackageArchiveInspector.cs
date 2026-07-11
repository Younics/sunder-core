using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Format;

public static class SunderPackageArchiveInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);
    private static readonly ArchiveRelativePath ManifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath);
    private static readonly ArchiveRelativePath ContentIndexPath = ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath);

    public static Task<SunderPackageArchiveValidationResult> ExtractAndValidateAsync(
        string packagePath,
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ExtractAndValidateAsync(
            packagePath,
            stagingPath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task<SunderPackageArchiveValidationResult> ExtractAndValidateAsync(
        string packagePath,
        string stagingPath,
        SunderArchiveExtractionOptions options)
    {
        await SunderArchive.ExtractAtomicAsync(packagePath, stagingPath, options);
        return await ValidateExtractedPackageAsync(stagingPath, options.MaxMetadataJsonBytes, options.CancellationToken);
    }

    public static void ExtractArchive(string packagePath, string stagingPath)
        => SunderArchive.ExtractAtomic(packagePath, stagingPath);

    public static Task<SunderPackageArchiveValidationResult> ValidateExtractedPackageAsync(
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ValidateExtractedPackageAsync(
            stagingPath,
            SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes,
            cancellationToken);

    public static async Task<SunderPackageArchiveValidationResult> ValidateExtractedPackageAsync(
        string stagingPath,
        int maxMetadataJsonBytes,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestFilePath = SunderArchive.ResolveFile(stagingPath, ManifestPath);
        var contentIndexFilePath = SunderArchive.ResolveFile(stagingPath, ContentIndexPath);

        if (!File.Exists(manifestFilePath))
        {
            errors.Add($"Package archive is missing {SunderPackageFormat.ManifestPath}.");
        }

        if (!File.Exists(contentIndexFilePath))
        {
            errors.Add($"Package archive is missing {SunderPackageFormat.ContentIndexPath}.");
        }

        if (errors.Count > 0)
        {
            return new SunderPackageArchiveValidationResult(null, warnings, errors);
        }

        SunderPackageManifest? manifest;
        SunderPackageContentIndex? contentIndex;
        try
        {
            manifest = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, ManifestPath, maxMetadataJsonBytes, cancellationToken),
                JsonContext.SunderPackageManifest);
            contentIndex = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, ContentIndexPath, maxMetadataJsonBytes, cancellationToken),
                JsonContext.SunderPackageContentIndex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            errors.Add($"Failed to parse package metadata: {ex.Message}");
            return new SunderPackageArchiveValidationResult(null, warnings, errors);
        }

        ValidateManifest(manifest, stagingPath, errors);
        await ValidateContentIndexAsync(contentIndex, stagingPath, errors, cancellationToken);
        return new SunderPackageArchiveValidationResult(errors.Count == 0 ? manifest : null, warnings, errors);
    }

    private static void ValidateManifest(SunderPackageManifest? manifest, string stagingPath, ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is empty or invalid.");
            return;
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
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' is missing name.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Package version '{manifest.Version}' must be strict SemVer 2.0.");
        }

        if (!TryParsePath(manifest.EntryAssembly, "entryAssembly", errors, out var entryAssembly))
        {
            if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            {
                errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' is missing entryAssembly.");
            }
        }
        else
        {
            var libraryPath = ArchiveRelativePath.Parse(SunderPackageFormat.LibraryPayloadRoot + entryAssembly);
            if (!File.Exists(SunderArchive.ResolveFile(stagingPath, libraryPath)))
            {
                errors.Add($"Package '{manifest.Id ?? stagingPath}' is missing entry assembly '{manifest.EntryAssembly}' under payload/lib/.");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Icon)
            && TryParsePath(manifest.Icon, "icon", errors, out var icon))
        {
            var iconPath = icon.ToString().StartsWith("assets/", StringComparison.Ordinal)
                ? ArchiveRelativePath.Parse("payload/" + icon)
                : icon;
            if (!File.Exists(SunderArchive.ResolveFile(stagingPath, iconPath)))
            {
                errors.Add($"Package icon '{manifest.Icon}' was not found in the package artifact.");
            }
        }

        var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (!PackageId.TryParse(dependency.PackageId, out _))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenDependencies.Add(dependency.PackageId!))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' is declared more than once.");
            }

            if (!PackageVersionRange.TryParse(dependency.VersionRange, out _))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' has unsupported versionRange '{dependency.VersionRange}'.");
            }
        }

        if (manifest.SdkApiVersion != SunderPackageFormat.CurrentSdkApiVersion)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' must declare sdkApiVersion {SunderPackageFormat.CurrentSdkApiVersion}.");
        }

        if (!SemanticVersion.TryParse(manifest.SdkPackageVersion, out _))
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' must declare a strict SemVer 2.0 sdkPackageVersion.");
        }

        var capabilities = manifest.RequiredSdkCapabilities;
        if (capabilities is null || capabilities.Count == 0)
        {
            errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' must declare requiredSdkCapabilities.");
        }
        else
        {
            var seenCapabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in capabilities)
            {
                if (!SunderPackageFormat.IsSdkCapabilityId(capability))
                {
                    errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' declares invalid SDK capability '{capability}'.");
                }
                else if (!seenCapabilities.Add(capability))
                {
                    errors.Add($"Package manifest for '{manifest.Id ?? stagingPath}' declares SDK capability '{capability}' more than once.");
                }
            }
        }
    }

    private static async Task ValidateContentIndexAsync(
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
            if (!TryParsePath(entry.Path, "content-index path", errors, out var path))
            {
                continue;
            }

            var normalizedPath = path.ToString();
            if (indexedPaths.TryGetValue(normalizedPath, out var existing))
            {
                var collision = string.Equals(existing, normalizedPath, StringComparison.Ordinal) ? "duplicate" : "case-colliding";
                errors.Add($"Package content index contains {collision} path '{normalizedPath}'.");
                continue;
            }

            indexedPaths.Add(normalizedPath, normalizedPath);
            if (string.Equals(normalizedPath, SunderPackageFormat.ContentIndexPath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Package content index must not index itself at '{normalizedPath}'.");
                continue;
            }

            if (entry.Size < 0)
            {
                errors.Add($"Package content index path '{normalizedPath}' has invalid size {entry.Size}.");
            }

            if (!IsLowercaseSha256(entry.Sha256))
            {
                errors.Add($"Package content index path '{normalizedPath}' must declare a lowercase SHA-256 hash.");
            }

            if (string.IsNullOrWhiteSpace(entry.Role))
            {
                errors.Add($"Package content index path '{normalizedPath}' must declare role.");
            }

            var filePath = SunderArchive.ResolveFile(stagingPath, path);
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

            await using var stream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
            {
                errors.Add($"Package file '{normalizedPath}' SHA-256 mismatch.");
            }
        }

        foreach (var actualFile in SunderArchive.EnumerateFiles(stagingPath))
        {
            var actualPath = actualFile.Path.ToString();
            if (!string.Equals(actualPath, SunderPackageFormat.ContentIndexPath, StringComparison.OrdinalIgnoreCase)
                && !indexedPaths.ContainsKey(actualPath))
            {
                errors.Add($"Package archive contains unindexed file '{actualPath}'.");
            }
        }
    }

    private static bool TryParsePath(
        string? value,
        string label,
        ICollection<string> errors,
        out ArchiveRelativePath path)
    {
        if (ArchiveRelativePath.TryParse(value, int.MaxValue, int.MaxValue, out path, out var error))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Package {label} '{value}' is unsafe: {error}.");
        }

        return false;
    }

    private static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
