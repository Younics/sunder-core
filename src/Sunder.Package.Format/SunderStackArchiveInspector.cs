using System.Text;
using System.Text.Json;

namespace Sunder.Package.Format;

public static class SunderStackArchiveInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);
    private static readonly ArchiveRelativePath ManifestPath = ArchiveRelativePath.Parse(SunderStackFormat.ManifestPath);
    private static readonly ArchiveRelativePath ContentIndexPath = ArchiveRelativePath.Parse(SunderStackFormat.ContentIndexPath);

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
        if (!File.Exists(SunderArchive.ResolveFile(stagingPath, ManifestPath)))
        {
            errors.Add($"Stack archive is missing {SunderStackFormat.ManifestPath}.");
        }

        if (!File.Exists(SunderArchive.ResolveFile(stagingPath, ContentIndexPath)))
        {
            errors.Add($"Stack archive is missing {SunderStackFormat.ContentIndexPath}.");
        }

        if (errors.Count > 0)
        {
            return new SunderStackArchiveValidationResult(null, warnings, errors);
        }

        SunderStackManifest? manifest;
        SunderStackContentIndex? contentIndex;
        try
        {
            manifest = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, ManifestPath, maxMetadataJsonBytes, cancellationToken),
                JsonContext.SunderStackManifest);
            contentIndex = JsonSerializer.Deserialize(
                await SunderArchive.ReadMetadataJsonAsync(stagingPath, ContentIndexPath, maxMetadataJsonBytes, cancellationToken),
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

        StackManifestSchemaValidator.Validate(manifest, stagingPath, errors);
        if (manifest is not null)
        {
            StackMediaValidator.Validate(manifest.Media, stagingPath, errors);
        }

        await StackContentIndexValidator.ValidateAsync(contentIndex, stagingPath, errors, cancellationToken);
        StackSecretValidator.Validate(stagingPath, errors);

        return new SunderStackArchiveValidationResult(
            errors.Count == 0 && manifest is not null ? SunderStackManifestProjection.Create(manifest) : null,
            warnings,
            errors);
    }
}
