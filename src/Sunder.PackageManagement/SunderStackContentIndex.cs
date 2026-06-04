using System.Text.Json.Serialization;

namespace Sunder.PackageManagement;

public sealed record SunderStackContentIndex(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("files")] IReadOnlyList<SunderStackContentIndexEntry> Files);

public sealed record SunderStackContentIndexEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);
