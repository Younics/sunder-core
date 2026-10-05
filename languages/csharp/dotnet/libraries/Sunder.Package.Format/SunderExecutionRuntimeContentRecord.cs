using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed record SunderExecutionRuntimeContentRecord
{
    [JsonPropertyName("path"), JsonRequired]
    public string? Path { get; init; }

    [JsonPropertyName("sha256"), JsonRequired]
    public string? Sha256 { get; init; }

    [JsonPropertyName("size"), JsonRequired]
    public long Size { get; init; }
}

internal sealed record SunderExecutionRuntimeContentIndex
{
    [JsonPropertyName("schemaVersion"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("files"), JsonRequired]
    public IReadOnlyList<SunderExecutionRuntimeContentRecord?>? Files { get; init; }
}
