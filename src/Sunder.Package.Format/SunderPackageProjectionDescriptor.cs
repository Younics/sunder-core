using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed record SunderPackageProjectionDescriptor
{
    [JsonPropertyName("projectionFormatVersion"), JsonRequired]
    public int ProjectionFormatVersion { get; init; }

    [JsonPropertyName("packageId"), JsonRequired]
    public string? PackageId { get; init; }

    [JsonPropertyName("packageVersion"), JsonRequired]
    public string? PackageVersion { get; init; }

    [JsonPropertyName("sourceArchiveSha256"), JsonRequired]
    public string? SourceArchiveSha256 { get; init; }

    [JsonPropertyName("kind"), JsonRequired]
    public string? Kind { get; init; }

    [JsonPropertyName("rid"), JsonRequired]
    public string? Rid { get; init; }

    [JsonPropertyName("manifestSha256"), JsonRequired]
    public string? ManifestSha256 { get; init; }

    [JsonPropertyName("projectionSha256"), JsonRequired]
    public string? ProjectionSha256 { get; init; }
}
