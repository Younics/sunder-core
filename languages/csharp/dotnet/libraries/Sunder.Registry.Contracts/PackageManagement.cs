namespace Sunder.Registry.Contracts;

/// <summary>Required database policy concurrency preconditions for management and authority mutations.</summary>
public static class RegistryPolicyMutationHeaders
{
    public const string IdempotencyKey = "X-Sunder-Idempotency-Key";
    public const string PackagePolicyRevision = "X-Sunder-Package-Policy-Revision";
    public const string PublicationAuthorityRevision = "X-Sunder-Publication-Authority-Revision";
}

public sealed record RegistryPublishLocalPackageRequest(
    string PackagePath,
    bool SetLatest = true,
    string InitialAvailability = RegistryPackageVersionAvailability.Listed);

/// <summary>Immutable accepted publication evidence for exact readback after an ambiguous upload result.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackagePublicationEvidence(
    [property: System.Text.Json.Serialization.JsonRequired] RegistryPackageVersionIdentity Package,
    [property: System.Text.Json.Serialization.JsonRequired] string OwnerPrincipalId,
    [property: System.Text.Json.Serialization.JsonRequired] string PublicInventorySha256,
    [property: System.Text.Json.Serialization.JsonRequired] string InitialAvailability);

public sealed record RegistryPublishPackageResponse(
    bool Success,
    string? PackageId,
    string? Version,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public string? ErrorCode { get; init; } = Success ? null : RegistryV1ErrorCodes.InvalidRequest;
}

/// <summary>A package maintainer identified by an opaque Registry principal ID, never a provider subject or handle.</summary>
public sealed record RegistryPackageMaintainer(
    string UserId,
    bool IsOwner,
    DateTimeOffset AddedAtUtc,
    string? Username = null,
    string? DisplayName = null,
    string? Email = null,
    string? AvatarUrl = null);

public sealed record RegistryPackageMaintainersResponse(
    string PackageId,
    IReadOnlyList<RegistryPackageMaintainer> Maintainers)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPolicyIdentity? PublicationAuthorityPolicy { get; init; }
}

/// <summary>Selects an existing Registry principal by its exact opaque ID.</summary>
public sealed record RegistryAddPackageMaintainerRequest(string UserId);

public sealed record RegistryPackageMaintainerOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}

public sealed record RegistryPackageStarResponse(
    bool Success,
    string? Message,
    RegistryPackageStats? Stats,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}

public sealed record RegistrySetPackageVersionYankRequest([property: System.Text.Json.Serialization.JsonRequired] bool IsYanked = true);

public sealed record RegistryDeprecatePackageVersionRequest(string? Message);

public sealed record RegistrySetPackageDistTagRequest(string Version);

public sealed record RegistryPackageDistTag(
    string Tag,
    string Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record RegistryPackageDistTagsResponse(
    string PackageId,
    IReadOnlyList<RegistryPackageDistTag> DistTags)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPolicyIdentity? PackagePolicy { get; init; }
}

public sealed record RegistryPackageManagementOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}
