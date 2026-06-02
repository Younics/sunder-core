using System.Text.Json.Serialization;

namespace Sunder.PackageManagement;

public sealed class SunderStackManifest
{
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    [JsonPropertyName("stackId")]
    public string? StackId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset? CreatedAtUtc { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<SunderStackPackageRequirement>? Packages { get; init; }

    [JsonPropertyName("fragments")]
    public IReadOnlyList<SunderStackFragmentManifest>? Fragments { get; init; }

    [JsonPropertyName("requiredInputs")]
    public IReadOnlyList<SunderStackRequiredInputManifest>? RequiredInputs { get; init; }

    [JsonPropertyName("safety")]
    public SunderStackSafetyManifest? Safety { get; init; }
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

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("sourceItemId")]
    public string? SourceItemId { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("defaultSelected")]
    public bool? DefaultSelected { get; init; }

    [JsonPropertyName("payloadPath")]
    public string? PayloadPath { get; init; }

    [JsonPropertyName("requiresPackages")]
    public IReadOnlyList<string>? RequiresPackages { get; init; }

    [JsonPropertyName("requiredInputs")]
    public IReadOnlyList<SunderStackRequiredInputManifest>? RequiredInputs { get; init; }

    [JsonPropertyName("displayDetails")]
    public IReadOnlyList<SunderStackFragmentDisplayDetail>? DisplayDetails { get; init; }

    [JsonPropertyName("safety")]
    public SunderStackSafetyManifest? Safety { get; init; }
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

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("required")]
    public bool? Required { get; init; }
}

public sealed class SunderStackSafetyManifest
{
    [JsonPropertyName("containsSecrets")]
    public bool? ContainsSecrets { get; init; }

    [JsonPropertyName("containsSecretReferences")]
    public bool? ContainsSecretReferences { get; init; }

    [JsonPropertyName("containsLocalPaths")]
    public bool? ContainsLocalPaths { get; init; }

    [JsonPropertyName("containsPrivateText")]
    public bool? ContainsPrivateText { get; init; }

    [JsonPropertyName("containsExecutableCommands")]
    public bool? ContainsExecutableCommands { get; init; }

    [JsonPropertyName("containsNetworkEndpoints")]
    public bool? ContainsNetworkEndpoints { get; init; }

    [JsonPropertyName("containsMachineSpecificValues")]
    public bool? ContainsMachineSpecificValues { get; init; }
}
