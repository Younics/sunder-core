using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed class SunderStackManifest
{
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    [JsonPropertyName("minReaderVersion")]
    public int? MinReaderVersion { get; init; }

    [JsonPropertyName("features")]
    public IReadOnlyList<string>? Features { get; init; }

    [JsonPropertyName("requiredFeatures")]
    public IReadOnlyList<string>? RequiredFeatures { get; init; }

    [JsonPropertyName("stackId")]
    public string? StackId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("readmeMarkdown")]
    public string? ReadmeMarkdown { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset? CreatedAtUtc { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<SunderStackPackageRequirement>? Packages { get; init; }

    [JsonPropertyName("fragments")]
    public IReadOnlyList<SunderStackFragmentManifest>? Fragments { get; init; }

    [JsonPropertyName("media")]
    public IReadOnlyList<SunderStackMediaManifest>? Media { get; init; }

}

public sealed class SunderStackMediaManifest
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("altText")]
    public string? AltText { get; init; }

    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; init; }
}

public sealed class SunderStackPackageRequirement
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("installTag")]
    public string? InstallTag { get; init; }

    [JsonPropertyName("createdWithVersion")]
    public string? CreatedWithVersion { get; init; }

    [JsonPropertyName("minimumVersion")]
    public string? MinimumVersion { get; init; }

    [JsonPropertyName("required")]
    public bool? Required { get; init; }
}

public sealed class SunderStackFragmentManifest
{
    [JsonPropertyName("fragmentId")]
    public string? FragmentId { get; init; }

    [JsonPropertyName("ownerPackageId")]
    public string? OwnerPackageId { get; init; }

    [JsonPropertyName("contributorId")]
    public string? ContributorId { get; init; }

    [JsonPropertyName("schemaId")]
    public string? SchemaId { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("defaultSelected")]
    public bool? DefaultSelected { get; init; }

    [JsonPropertyName("payloadPath")]
    public string? PayloadPath { get; init; }

    [JsonPropertyName("requiredInputs")]
    public IReadOnlyList<SunderStackRequiredInputManifest>? RequiredInputs { get; init; }

    [JsonPropertyName("preview")]
    public SunderStackFragmentPreview? Preview { get; init; }
}

public sealed class SunderStackFragmentPreview
{
    [JsonPropertyName("sourceItemId")]
    public string? SourceItemId { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("displayDetails")]
    public IReadOnlyList<SunderStackFragmentDisplayDetail>? DisplayDetails { get; init; }
}

public sealed class SunderStackFragmentDisplayDetail
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("behavior")]
    public string? Behavior { get; init; }
}

public sealed class SunderStackRequiredInputManifest
{
    [JsonPropertyName("inputId")]
    public string? InputId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("sensitivity")]
    public string? Sensitivity { get; init; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; init; }

    [JsonPropertyName("required")]
    public bool? Required { get; init; }
}
