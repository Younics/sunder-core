namespace Sunder.Registry.Contracts;

/// <summary>An exact descriptor identity in a verified immutable Core platform catalog.</summary>
public sealed record RegistryPlatformContractDescriptorV1(string ContractId, string ContractVersion,
    int DescriptorBytes, string DescriptorSha256, string AbiSha256);

/// <summary>Registry-authoritative catalog metadata. Clients verify exact downloaded bytes and descriptor identities.</summary>
public sealed record RegistryPlatformContractReleaseV1(string AuthorityId, string ReleaseVersion,
    string CatalogSha256, long CatalogBytes, string CatalogDownloadUrl,
    string LifecycleState, DateTimeOffset PublishedAtUtc, IReadOnlyList<RegistryPlatformContractDescriptorV1> Contracts,
    DateTimeOffset? RevokedAtUtc = null);

/// <summary>Revokes exactly the observed immutable Core catalog; revocation is terminal.</summary>
public sealed record RegistryRevokePlatformContractReleaseRequest(string ExpectedCatalogSha256);

public sealed record RegistryPlatformContractOperationResponse(bool Success, RegistryPlatformContractReleaseV1? Release,
    string? ErrorCode, IReadOnlyList<string> Errors);
