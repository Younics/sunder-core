using System.Text.Json.Serialization;

namespace Sunder.PackageManagement;

public sealed record SunderPackageContentIndex(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("files")] IReadOnlyList<SunderPackageContentIndexEntry> Files);

public sealed record SunderPackageContentIndexEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("role")] string Role);
