using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

/// <summary>Reads supported Stack metadata without tying readers to the current writer version.</summary>
public static class SunderStackMetadataReader
{
    private static readonly SunderPackageJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    });

    public static SunderStackManifest ReadManifest(string json)
        => ReadManifest(json, allowRetiredSchema: false);

    /// <summary>Reads retained Registry metadata using a known reader, without re-admitting a retired format for import.</summary>
    public static SunderStackManifest ReadRetainedManifest(string json)
        => ReadManifest(json, allowRetiredSchema: true);

    private static SunderStackManifest ReadManifest(string json, bool allowRetiredSchema)
    {
        var version = ReadVersion(json);
        if (!allowRetiredSchema && !SunderStackFormat.SupportsSchemaVersion(version))
            throw new NotSupportedException($"Unsupported Stack schemaVersion {version}. Supported versions: {string.Join(", ", SunderStackFormat.SupportedSchemaVersions)}.");

        // Add a real reader here when introducing another format; never interpret it as V1.
        return version switch
        {
            1 => StrictJsonSerializer.Deserialize(json, JsonContext.SunderStackManifest)
                 ?? throw new JsonException("Stack manifest is empty."),
            _ => throw new NotSupportedException($"No Stack reader exists for schemaVersion {version}."),
        };
    }

    public static SunderStackContentIndex ReadContentIndex(string json)
    {
        var version = ReadVersion(json);
        if (!SunderStackFormat.SupportsContentIndexVersion(version))
            throw new NotSupportedException($"Unsupported Stack content-index schemaVersion {version}. Supported versions: {string.Join(", ", SunderStackFormat.SupportedContentIndexVersions)}.");

        return version switch
        {
            1 => StrictJsonSerializer.Deserialize(json, JsonContext.SunderStackContentIndex)
                 ?? throw new JsonException("Stack content index is empty."),
            _ => throw new NotSupportedException($"No Stack content-index reader exists for schemaVersion {version}."),
        };
    }

    /// <summary>Checks system compatibility independently of payload and archive validation.</summary>
    public static IReadOnlyList<string> GetCompatibilityErrors(SunderStackManifest manifest, bool allowRetiredSchema = false)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion is not { } version || !(allowRetiredSchema
                ? SunderStackFormat.KnownSchemaVersions.Contains(version)
                : SunderStackFormat.SupportsSchemaVersion(version)))
            errors.Add($"Unsupported Stack schemaVersion {manifest.SchemaVersion}. Supported versions: {string.Join(", ", SunderStackFormat.SupportedSchemaVersions)}.");
        if (manifest.MinReaderVersion > SunderStackFormat.CurrentReaderVersion)
            errors.Add($"Stack manifest requires reader version {manifest.MinReaderVersion}, but this Sunder reader supports {SunderStackFormat.CurrentReaderVersion}.");
        foreach (var feature in manifest.RequiredFeatures ?? [])
            if (feature is not null && !SunderStackFormat.SupportedFeatures.Contains(feature))
                errors.Add($"Stack requires unsupported feature '{feature}'.");
        return errors;
    }

    private static int ReadVersion(string json)
    {
        using var document = StrictJsonSerializer.ParseDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var version) || version <= 0)
            throw new JsonException("Stack metadata must declare a positive integer schemaVersion.");
        return version;
    }
}
