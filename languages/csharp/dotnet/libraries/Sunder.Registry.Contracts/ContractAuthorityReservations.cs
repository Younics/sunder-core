namespace Sunder.Registry.Contracts;

public static class RegistryContractProviderPublicationPolicies
{
    public const string Open = "Open";
    public const string AuthorityPackageOnly = "AuthorityPackageOnly";
    public const string AllowlistedPackages = "AllowlistedPackages";
}

/// <summary>A contract-family lease or permanent claim bound to an owned package and opaque Registry principal. Claimed authority has no expiry.</summary>
public sealed record RegistryContractAuthorityReservation(
    string ContractId, string OwningPackageId, Guid OwningPackageLeaseId, Guid LeaseId,
    long Revision, string OwnerPrincipalId, string State, string ProviderPublicationPolicy,
    long ProviderPolicyRevision, IReadOnlyList<string> AllowedProviderPackageIds,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RenewedAtUtc, DateTimeOffset? CancelledAtUtc, DateTimeOffset? ClaimedAtUtc = null);

public sealed record RegistryReserveContractAuthorityRequest(
    string OwningPackageId,
    string ProviderPublicationPolicy = RegistryContractProviderPublicationPolicies.Open,
    IReadOnlyList<string>? AllowedProviderPackageIds = null);

public sealed record RegistrySetContractProviderPolicyRequest(
    Guid LeaseId, long ExpectedRevision, string ProviderPublicationPolicy,
    IReadOnlyList<string>? AllowedProviderPackageIds = null);

public sealed record RegistryContractAuthorityOperationResponse(
    bool Success, RegistryContractAuthorityReservation? Reservation, string? ErrorCode,
    IReadOnlyList<string> Errors);
