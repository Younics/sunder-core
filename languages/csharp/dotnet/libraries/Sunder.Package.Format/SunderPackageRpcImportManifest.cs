using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed class SunderPackageArtifactImportManifest
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("requestedVersionRange")]
    public string? RequestedVersionRange { get; init; }

    [JsonPropertyName("includePrerelease")]
    public bool? IncludePrerelease { get; init; }

    [JsonPropertyName("selectedPackageVersion")]
    public string? SelectedPackageVersion { get; init; }

    [JsonPropertyName("selectedPackageContentIdentity")]
    public string? SelectedPackageContentIdentity { get; init; }

    [JsonPropertyName("publicContractCount")]
    public int? PublicContractCount { get; init; }

    [JsonPropertyName("publicInventorySha256")]
    public string? PublicInventorySha256 { get; init; }

    [JsonPropertyName("authorityConstraints")]
    public IReadOnlyList<SunderPackageAuthorityConstraintManifest?>? AuthorityConstraints { get; init; }
}

public sealed class SunderPackageApiImportManifest
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("presence")]
    public string? Presence { get; init; }

    [JsonPropertyName("bundleRefs")]
    public IReadOnlyList<SunderPackageContractReferenceManifest?>? BundleRefs { get; init; }

    [JsonPropertyName("useRefs")]
    public IReadOnlyList<SunderPackageContractUseReferenceManifest?>? UseRefs { get; init; }
}

public sealed class SunderPackageProviderImportManifest
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }

    [JsonPropertyName("bundleRef")]
    public SunderPackageContractReferenceManifest? BundleRef { get; init; }

    [JsonPropertyName("providerRefs")]
    public IReadOnlyList<SunderPackageProviderReferenceManifest?>? ProviderRefs { get; init; }
}

public sealed class SunderPackagePlatformImportManifest
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("authorityKind")]
    public string? AuthorityKind { get; init; }

    [JsonPropertyName("authorityId")]
    public string? AuthorityId { get; init; }

    [JsonPropertyName("authorityReleaseRange")]
    public string? AuthorityReleaseRange { get; init; }

    [JsonPropertyName("includePrerelease")]
    public bool? IncludePrerelease { get; init; }

    [JsonPropertyName("selectedAuthorityRelease")]
    public string? SelectedAuthorityRelease { get; init; }

    [JsonPropertyName("selectedAuthorityContentIdentity")]
    public string? SelectedAuthorityContentIdentity { get; init; }

    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }

    [JsonPropertyName("bundleRef")]
    public SunderPackageContractReferenceManifest? BundleRef { get; init; }

    [JsonPropertyName("providerRefs")]
    public IReadOnlyList<SunderPackageProviderReferenceManifest?>? ProviderRefs { get; init; }
}

public sealed class SunderPackageAuthorityConstraintManifest
{
    [JsonPropertyName("authorityKind")]
    public string? AuthorityKind { get; init; }

    [JsonPropertyName("authorityId")]
    public string? AuthorityId { get; init; }

    [JsonPropertyName("selectedAuthorityRelease")]
    public string? SelectedAuthorityRelease { get; init; }

    [JsonPropertyName("selectedAuthorityContentIdentity")]
    public string? SelectedAuthorityContentIdentity { get; init; }

}

public sealed class SunderPackageContractReferenceManifest
{
    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }
}

public sealed class SunderPackageContractUseReferenceManifest
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }
}

public sealed class SunderPackageProviderReferenceManifest
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("contractId")]
    public string? ContractId { get; init; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; init; }
}
