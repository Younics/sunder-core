using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Package.Format;

public static class SunderStackArchiveInspector
{
    private const long MaxMediaSize = 10L * 1024L * 1024L;
    private static readonly Regex StackIdRegex = new("^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FragmentIdRegex = new("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);
    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    public static Task<SunderStackArchiveValidationResult> ExtractAndValidateAsync(
        string stackPath,
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ExtractAndValidateAsync(
            stackPath,
            stagingPath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task<SunderStackArchiveValidationResult> ExtractAndValidateAsync(
        string stackPath,
        string stagingPath,
        SunderArchiveExtractionOptions options)
    {
        await SunderArchive.ExtractAtomicAsync(stackPath, stagingPath, options);
        return await ValidateExtractedStackAsync(stagingPath, options.MaxMetadataJsonBytes, options.CancellationToken);
    }

    public static void ExtractArchive(string stackPath, string stagingPath)
        => SunderArchive.ExtractAtomic(stackPath, stagingPath);

    public static Task<SunderStackArchiveValidationResult> ValidateExtractedStackAsync(
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ValidateExtractedStackAsync(
            stagingPath,
            SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes,
            cancellationToken);

    public static async Task<SunderStackArchiveValidationResult> ValidateExtractedStackAsync(
        string stagingPath,
        int maxMetadataJsonBytes,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestRelativePath = ArchiveRelativePath.Parse(SunderStackFormat.ManifestPath);
        var contentIndexRelativePath = ArchiveRelativePath.Parse(SunderStackFormat.ContentIndexPath);
        var manifestPath = SunderArchive.ResolveFile(stagingPath, manifestRelativePath);
        var contentIndexPath = SunderArchive.ResolveFile(stagingPath, contentIndexRelativePath);

        if (!File.Exists(manifestPath))
        {
            errors.Add($"Stack archive is missing {SunderStackFormat.ManifestPath}.");
        }

        if (!File.Exists(contentIndexPath))
        {
            errors.Add($"Stack archive is missing {SunderStackFormat.ContentIndexPath}.");
        }

        if (errors.Count > 0)
        {
            return new SunderStackArchiveValidationResult(null, warnings, errors);
        }

        SunderStackManifest? manifest = null;
        SunderStackContentIndex? contentIndex = null;
        try
        {
            manifest = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, manifestRelativePath, maxMetadataJsonBytes, cancellationToken),
                JsonContext.SunderStackManifest);
            contentIndex = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, contentIndexRelativePath, maxMetadataJsonBytes, cancellationToken),
                JsonContext.SunderStackContentIndex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            errors.Add($"Failed to parse Stack metadata: {ex.Message}");
            return new SunderStackArchiveValidationResult(null, warnings, errors);
        }

        ValidateManifest(manifest, stagingPath, warnings, errors);
        await ValidateContentIndexAsync(contentIndex, stagingPath, errors, cancellationToken);
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

        if (manifest.SchemaVersion != SunderStackFormat.CurrentSchemaVersion)
        {
            errors.Add($"Stack manifest must declare schemaVersion {SunderStackFormat.CurrentSchemaVersion}.");
        }

        if (manifest.MinReaderVersion is not null && manifest.MinReaderVersion > SunderStackFormat.CurrentReaderVersion)
        {
            errors.Add($"Stack manifest requires reader version {manifest.MinReaderVersion}, but this Sunder reader supports {SunderStackFormat.CurrentReaderVersion}.");
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
        ValidateMedia(manifest.Media ?? [], stagingPath, errors);
    }

    private static void ValidatePackageRequirements(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        ICollection<string> errors)
    {
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!PackageId.TryParse(package.PackageId, out _))
            {
                errors.Add($"Stack package id '{package.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenPackages.Add(package.PackageId!))
            {
                errors.Add($"Stack package id '{package.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(package.InstallTag) || !TagRegex.IsMatch(package.InstallTag))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' must declare a valid installTag.");
            }

            if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion) && !SemanticVersion.TryParse(package.CreatedWithVersion, out _))
            {
                errors.Add($"Stack package '{package.PackageId ?? "unknown"}' has invalid createdWithVersion '{package.CreatedWithVersion}'.");
            }

            if (!string.IsNullOrWhiteSpace(package.MinimumVersion) && !SemanticVersion.TryParse(package.MinimumVersion, out _))
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

            if (!PackageId.TryParse(fragment.OwnerPackageId, out _))
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
                var payloadPath = fragment.PayloadPath;
                if (!ValidateRelativePath(payloadPath, "fragment payloadPath", errors))
                {
                    continue;
                }
                if (!payloadPath.StartsWith("payload/", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' must be under payload/.");
                }
                else if (!File.Exists(SunderArchive.ResolveFile(stagingPath, ArchiveRelativePath.Parse(payloadPath))))
                {
                    errors.Add($"Stack fragment '{fragmentId}' payloadPath '{fragment.PayloadPath}' was not found in the Stack archive.");
                }
            }

            ValidateRequiredInputs(fragment.RequiredInputs ?? [], $"Stack fragment '{fragmentId}'", errors);
        }
    }

    private static void ValidateMedia(
        IReadOnlyList<SunderStackMediaManifest> mediaItems,
        string stagingPath,
        ICollection<string> errors)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in mediaItems)
        {
            var label = string.IsNullOrWhiteSpace(media.FileName) ? media.Path ?? "unknown" : media.FileName;
            if (string.IsNullOrWhiteSpace(media.Path))
            {
                errors.Add($"Stack media '{label}' is missing path.");
                continue;
            }

            var mediaPath = media.Path;
            if (!ValidateRelativePath(mediaPath, "media path", errors))
            {
                continue;
            }
            if (!mediaPath.StartsWith(SunderStackFormat.MediaPayloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Stack media '{label}' path '{media.Path}' must be under {SunderStackFormat.MediaPayloadRoot}.");
            }
            else if (!seenPaths.Add(mediaPath))
            {
                errors.Add($"Stack media path '{mediaPath}' is declared more than once.");
            }

            var filePath = SunderArchive.ResolveFile(stagingPath, ArchiveRelativePath.Parse(mediaPath));
            if (!File.Exists(filePath))
            {
                errors.Add($"Stack media '{label}' path '{media.Path}' was not found in the Stack archive.");
                continue;
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length <= 0)
            {
                errors.Add($"Stack media '{label}' is empty.");
            }
            else if (fileInfo.Length > MaxMediaSize)
            {
                errors.Add($"Stack media '{label}' must be 10 MB or smaller.");
            }

            if (media.Size is null or <= 0)
            {
                errors.Add($"Stack media '{label}' must declare size.");
            }
            else if (media.Size != fileInfo.Length)
            {
                errors.Add($"Stack media '{label}' size mismatch.");
            }

            if (string.IsNullOrWhiteSpace(media.FileName) || media.FileName != Path.GetFileName(media.FileName))
            {
                errors.Add($"Stack media '{label}' must declare a fileName without path separators.");
            }

            if (string.IsNullOrWhiteSpace(media.ContentType) || !AllowedMediaTypes.Contains(media.ContentType))
            {
                errors.Add($"Stack media '{label}' must be a PNG, JPEG, WebP, or GIF image.");
            }

            if (media.SortOrder is < 0)
            {
                errors.Add($"Stack media '{label}' sortOrder must not be negative.");
            }

            if (media.AltText?.Length > 500)
            {
                errors.Add($"Stack media '{label}' altText must be 500 characters or shorter.");
            }
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

    private static async Task ValidateContentIndexAsync(
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
            if (!ValidateRelativePath(entry.Path, "content-index path", errors))
            {
                continue;
            }

            var normalizedPath = entry.Path!;
            if (indexedPaths.TryGetValue(normalizedPath, out var existing))
            {
                var collision = string.Equals(existing, normalizedPath, StringComparison.Ordinal) ? "duplicate" : "case-colliding";
                errors.Add($"Stack content index contains {collision} path '{normalizedPath}'.");
                continue;
            }

            indexedPaths.Add(normalizedPath, normalizedPath);

            if (string.Equals(normalizedPath, SunderStackFormat.ContentIndexPath, StringComparison.OrdinalIgnoreCase))
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

            var filePath = SunderArchive.ResolveFile(stagingPath, ArchiveRelativePath.Parse(normalizedPath));
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

            await using var stream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
            {
                errors.Add($"Stack file '{normalizedPath}' SHA-256 mismatch.");
            }
        }

        foreach (var actualFileEntry in SunderArchive.EnumerateFiles(stagingPath))
        {
            var actualFile = actualFileEntry.Path.ToString();
            if (string.Equals(actualFile, SunderStackFormat.ContentIndexPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!indexedPaths.ContainsKey(actualFile))
            {
                errors.Add($"Stack archive contains unindexed file '{actualFile}'.");
            }
        }
    }

    private static bool ValidateRelativePath(string? path, string label, ICollection<string> errors)
    {
        if (!ArchiveRelativePath.TryParse(path, int.MaxValue, int.MaxValue, out _, out var error))
        {
            errors.Add($"Stack {label} '{path}' is unsafe: {error}.");
            return false;
        }

        return true;
    }

    private static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
