namespace Sunder.Registry.Shared;

public sealed record RegistryPackageSummary(
    string PackageId,
    string Name,
    string? Summary,
    string? LatestVersion,
    string? IconUrl,
    bool IsYanked,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RegistryPackageSearchResult(
    IReadOnlyList<RegistryPackageSummary> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record RegistryPackageDetails(
    string PackageId,
    string Name,
    string? Summary,
    string? LatestVersion,
    string? IconUrl,
    IReadOnlyList<RegistryPackageVersionSummary> Versions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryPackageProfile? Profile = null);

public sealed record RegistryPackageVersionSummary(
    string Version,
    bool IsYanked,
    string? DeprecatedMessage,
    DateTimeOffset PublishedAtUtc);

public sealed record RegistryPackageVersionDetails(
    string PackageId,
    string Name,
    string? Summary,
    string Version,
    string? Icon,
    string EntryAssembly,
    string? SdkVersion,
    string? TargetFramework,
    bool IsYanked,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    RegistryPackageArtifact Artifact,
    DateTimeOffset PublishedAtUtc);

public sealed record RegistryPackageDependency(
    string PackageId,
    string VersionRange);

public sealed record RegistryPackageArtifact(
    string Sha256,
    long Size,
    string DownloadUrl);

public sealed record RegistryPackageProfile(
    string PackageId,
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RegistryPackageMedia> Media,
    DateTimeOffset? UpdatedAtUtc);

public sealed record RegistryPackageMedia(
    Guid MediaId,
    string FileName,
    string ContentType,
    long Size,
    string? AltText,
    int SortOrder,
    string Url);

public sealed record RegistryUpdatePackageProfileRequest(
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string>? Tags);

public sealed record RegistryPackageProfileOperationResponse(
    bool Success,
    string? Message,
    RegistryPackageProfile? Profile,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryPackageResolveResponse(
    string PackageId,
    string Version,
    string? DeprecatedMessage,
    RegistryPackageArtifact Artifact);

public sealed record RegistryResolveUpdatesRequest(
    IReadOnlyList<RegistryInstalledPackage> InstalledPackages,
    bool IncludePrerelease = false);

public sealed record RegistryInstalledPackage(
    string PackageId,
    string Version);

public sealed record RegistryResolveUpdatesResponse(
    IReadOnlyList<RegistryPackageUpdate> Updates);

public sealed record RegistryPackageUpdate(
    string PackageId,
    string CurrentVersion,
    string AvailableVersion,
    string? DeprecatedMessage,
    RegistryPackageArtifact Artifact);

public sealed record RegistryResolveInstallPlanRequest(
    string PackageId,
    string? Version,
    string? Tag,
    IReadOnlyList<RegistryInstalledPackageState> InstalledPackages,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false);

public sealed record RegistryInstalledPackageState(
    string PackageId,
    string Version,
    IReadOnlyList<RegistryPackageDependency> DependsOn);

public sealed record RegistryPackageInstallPlanItem(
    string PackageId,
    string? CurrentVersion,
    string Version,
    bool IsUpdate,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    RegistryPackageArtifact Artifact);

public sealed record RegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string Message);

public sealed record RegistryResolveInstallPlanResponse(
    bool Success,
    IReadOnlyList<RegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RegistryPackageInstallPlanConflict> Conflicts);

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
}

public sealed record RegistryPackageMaintainer(
    string UserId,
    bool IsOwner,
    DateTimeOffset AddedAtUtc,
    string? Username = null,
    string? DisplayName = null,
    string? Email = null);

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

public sealed record RegistryCurrentUserResponse(
    string UserId,
    string? DisplayName,
    string? Email);

public sealed record RegistryUserProfileSummary(
    string UserId,
    string? Username,
    string? DisplayName,
    string? Email,
    DateTimeOffset LastSeenAtUtc);

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
    DateTimeOffset UpdatedAtUtc);

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

public sealed record RegistryCliAuthorizeRequest(
    string RedirectUri,
    string State,
    string CodeChallenge,
    string? DisplayName = null);

public sealed record RegistryCliAuthorizeResponse(
    string RedirectUri,
    string Code,
    string State);

public sealed record RegistryCliTokenRequest(
    string Code,
    string CodeVerifier);

public sealed record RegistryCliTokenResponse(
    bool Success,
    string? Token,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<string> Errors);
