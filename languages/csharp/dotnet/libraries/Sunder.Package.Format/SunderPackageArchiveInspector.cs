using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public static class SunderPackageArchiveInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
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
        => await ValidateExtractedPackageCoreAsync(
            stagingPath,
            maxMetadataJsonBytes,
            cancellationToken,
            ioObserver: null);

    internal static Task<SunderPackageArchiveValidationResult> ValidateExtractedPackageAsync(
        string stagingPath,
        CancellationToken cancellationToken,
        ISunderPackageValidationIoObserver ioObserver)
        => ValidateExtractedPackageCoreAsync(
            stagingPath,
            SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes,
            cancellationToken,
            ioObserver);

    // Host-owned installed projections retain the complete source manifest. Their
    // acquisition scope is supplied by the trusted installation transaction, never
    // inferred from missing files or accepted from an ordinary package archive.
    internal static Task<SunderPackageArchiveValidationResult> ValidateExtractedProjectionSetAsync(
        string stagingPath,
        IReadOnlyList<SunderPackageTargetKey> acquiredTargets,
        CancellationToken cancellationToken = default,
        ISunderPackageValidationIoObserver? ioObserver = null)
    {
        ArgumentNullException.ThrowIfNull(acquiredTargets);
        return ValidateExtractedPackageCoreAsync(stagingPath,
            SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes, cancellationToken, ioObserver, acquiredTargets);
    }

    internal static async Task<SunderPackageArchiveValidationResult> ExtractAndValidateProjectionSetAsync(
        string packagePath,
        string stagingPath,
        IReadOnlyList<SunderPackageTargetKey> acquiredTargets,
        CancellationToken cancellationToken = default)
    {
        await SunderArchive.ExtractAtomicAsync(packagePath, stagingPath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });
        return await ValidateExtractedProjectionSetAsync(stagingPath, acquiredTargets, cancellationToken);
    }

    private static async Task<SunderPackageArchiveValidationResult> ValidateExtractedPackageCoreAsync(
        string stagingPath,
        int maxMetadataJsonBytes,
        CancellationToken cancellationToken,
        ISunderPackageValidationIoObserver? ioObserver,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
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
            manifest = StrictJsonSerializer.Deserialize(
                await SunderArchiveMetadataReader.ReadJsonAsync(
                    stagingPath,
                    ManifestPath,
                    maxMetadataJsonBytes,
                    cancellationToken,
                    ioObserver),
                JsonContext.SunderPackageManifest);
            contentIndex = StrictJsonSerializer.Deserialize(
                await SunderArchiveMetadataReader.ReadJsonAsync(
                    stagingPath,
                    ContentIndexPath,
                    maxMetadataJsonBytes,
                    cancellationToken,
                    ioObserver),
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

        if (acquiredTargets is not null && (acquiredTargets.Count > SunderPackageFormat.MaxTargets
            || acquiredTargets.Distinct().Count() != acquiredTargets.Count
            || acquiredTargets.Any(key => !SunderPackageTargetKey.TryCreate(key.Role, key.Rid, out _))))
        {
            errors.Add("Installed projection acquisition targets are invalid or duplicated.");
        }
        SunderPackageManifestValidator.ValidateCore(manifest, stagingPath, errors, acquiredTargets?.ToHashSet());
        await PackageContentIndexValidator.ValidateAsync(
            contentIndex,
            stagingPath,
            errors,
            cancellationToken,
            ioObserver);
        return new SunderPackageArchiveValidationResult(errors.Count == 0 ? manifest : null, warnings, errors)
        {
            ContentIndex = errors.Count == 0 ? contentIndex : null,
        };
    }
}
