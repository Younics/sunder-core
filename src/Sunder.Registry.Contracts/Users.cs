namespace Sunder.Registry.Contracts;

public sealed record RegistryUserAttribution(
    string? Username,
    string? DisplayName,
    DateTimeOffset? AddedAtUtc = null,
    bool IsOwner = false,
    string? AvatarUrl = null);

public sealed record RegistryCurrentUserResponse(
    string UserId,
    string? DisplayName,
    string? Email,
    string? Username = null,
    string? AvatarUrl = null,
    bool RequiresUsername = false);

public sealed record RegistryUpdateCurrentUserProfileRequest(
    string? Username,
    string? DisplayName,
    string? Email,
    string? AvatarUrl);

public sealed record RegistryUserProfileSummary(
    string UserId,
    string? Username,
    string? DisplayName,
    string? Email,
    DateTimeOffset LastSeenAtUtc,
    string? AvatarUrl = null);

public sealed record RegistryUserPackageSummary(
    string PackageId,
    string Name,
    string? Summary,
    string? LatestVersion,
    string? IconUrl,
    string Role,
    int VersionCount,
    int YankedVersionCount,
    int DeprecatedVersionCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryUserAttribution? Creator = null,
    RegistryPackageStats? Stats = null,
    bool IsYanked = false);

public sealed record RegistryUserPackageDetails(
    string PackageId,
    string Name,
    string? Summary,
    string? LatestVersion,
    string? IconUrl,
    string Role,
    IReadOnlyList<RegistryPackageVersionSummary> Versions,
    IReadOnlyList<RegistryPackageDistTag> DistTags,
    IReadOnlyList<RegistryPackageMaintainer> Maintainers,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryPackageProfile? Profile = null);
