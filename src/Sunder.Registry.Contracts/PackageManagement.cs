namespace Sunder.Registry.Contracts;

public sealed record RegistryPublishLocalPackageRequest(
    string PackagePath,
    bool SetLatest = true);

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
    IReadOnlyList<RegistryPackageMaintainer> Maintainers);

public sealed record RegistryAddPackageMaintainerRequest(string UserId);

public sealed record RegistryPackageMaintainerOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryPackageStarResponse(
    bool Success,
    string? Message,
    RegistryPackageStats? Stats,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistrySetPackageVersionYankRequest(bool IsYanked = true);

public sealed record RegistryDeprecatePackageVersionRequest(string? Message);

public sealed record RegistrySetPackageDistTagRequest(string Version);

public sealed record RegistryPackageDistTag(
    string Tag,
    string Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record RegistryPackageDistTagsResponse(
    string PackageId,
    IReadOnlyList<RegistryPackageDistTag> DistTags);

public sealed record RegistryPackageManagementOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}
