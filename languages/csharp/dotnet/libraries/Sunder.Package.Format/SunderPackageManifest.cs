using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed class SunderPackageManifest
{
    [JsonPropertyName("archiveFormatVersion")]
    public int? ArchiveFormatVersion { get; init; }

    [JsonPropertyName("manifestVersion")]
    public int? ManifestVersion { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<SunderPackageDependencyManifest>? DependsOn { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<SunderPackageTargetManifest?>? Targets { get; init; }

    [JsonPropertyName("contractBundles")]
    public IReadOnlyList<SunderPackageContractBundleManifest?>? ContractBundles { get; init; }

    [JsonPropertyName("usesContracts")]
    public IReadOnlyList<SunderPackageContractUseManifest?>? UsesContracts { get; init; }

    [JsonPropertyName("provides")]
    public IReadOnlyList<SunderPackageProviderManifest?>? Provides { get; init; }
}

public sealed class SunderPackageDependencyManifest
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("versionRange")]
    public string? VersionRange { get; init; }
}

public sealed class SunderPackageTargetManifest
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("rid")]
    public string? Rid { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("entryPoint")]
    public string? EntryPoint { get; init; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; init; }

    [JsonPropertyName("sdkVersion")]
    public string? SdkVersion { get; init; }

    [JsonPropertyName("requiredHostCapabilities")]
    public IReadOnlyList<string?>? RequiredHostCapabilities { get; init; }

    [JsonPropertyName("views")]
    public IReadOnlyList<SunderPackageWebViewManifest?>? Views { get; init; }
}

public sealed class SunderPackageWebViewManifest
{
    [JsonPropertyName("viewId")]
    public string? ViewId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("defaultPlacement")]
    public string? DefaultPlacement { get; init; }

    [JsonPropertyName("showInHotbar")]
    public bool? ShowInHotbar { get; init; }
}

public sealed class SunderPackageContractBundleManifest
{
    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("descriptorPath")]
    public string? DescriptorPath { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}

public sealed class SunderPackageContractUseManifest
{
    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("versionRange")]
    public string? VersionRange { get; init; }

    [JsonPropertyName("required")]
    public bool? Required { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<string?>? Actions { get; init; }
}

public sealed class SunderPackageProviderManifest
{
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }

    [JsonPropertyName("contractSha256")]
    public string? ContractSha256 { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}
