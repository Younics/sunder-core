namespace Sunder.Registry.Shared;

public enum RegistrySearchSort
{
    Updated,
    Downloads,
    Stars,
}

public sealed record RegistryUserAttribution(
    string? Username,
    string? DisplayName,
    DateTimeOffset? AddedAtUtc = null,
    bool IsOwner = false,
    string? AvatarUrl = null);

public sealed record RegistryPackageSummary(
    string PackageId,
    string Name,
    string? Summary,
    string? LatestVersion,
    string? IconUrl,
    bool IsYanked,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryPackageStats? Stats = null,
    RegistryUserAttribution? Creator = null);

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
    RegistryPackageProfile? Profile = null,
    RegistryPackageStats? Stats = null,
    IReadOnlyList<RegistryPackageDependent>? Dependents = null,
    RegistryUserAttribution? Creator = null,
    IReadOnlyList<RegistryUserAttribution>? Maintainers = null);

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

/// <summary>A package that declares a dependency on the package being viewed (a reverse dependency).</summary>
public sealed record RegistryPackageDependent(
    string PackageId,
    string Name,
    string VersionRange);

/// <summary>Download count for a single calendar day, used to plot the downloads chart.</summary>
public sealed record RegistryPackageDownloadPoint(
    DateOnly Date,
    long Count);

/// <summary>Aggregate usage statistics shown on the public package detail page.</summary>
public sealed record RegistryPackageStats(
    long TotalDownloads,
    long WeeklyDownloads,
    long TotalViews,
    IReadOnlyList<RegistryPackageDownloadPoint> DailyDownloads,
    long Stars = 0,
    bool IsStarred = false);

public sealed record RegistryPackageViewResponse(long Views);

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

public sealed record RegistryPackageChangeRequest(
    string PackageId,
    string? Version,
    string? Tag = null);

public sealed record RegistryResolvePackageChangesRequest(
    IReadOnlyList<RegistryPackageChangeRequest> Packages,
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

public sealed record RegistryStackSummary(
    string StackId,
    string Name,
    string? Summary,
    int PackageCount,
    int FragmentCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryStackStats? Stats = null,
    RegistryUserAttribution? Creator = null);

public sealed record RegistryStackSearchResult(
    IReadOnlyList<RegistryStackSummary> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record RegistryStackDetails(
    string StackId,
    string Name,
    string? Summary,
    IReadOnlyList<RegistryStackPackageRequirement> Packages,
    IReadOnlyList<RegistryStackFragmentSummary> Fragments,
    IReadOnlyList<RegistryStackRequiredInput> RequiredInputs,
    RegistryStackArtifact Artifact,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryStackStats? Stats = null,
    RegistryStackProfile? Profile = null,
    RegistryUserAttribution? Creator = null,
    IReadOnlyList<RegistryUserAttribution>? Maintainers = null);

public sealed record RegistryStackStats(
    long TotalDownloads,
    long Stars,
    bool IsStarred,
    long WeeklyDownloads = 0,
    IReadOnlyList<RegistryStackDownloadPoint>? DailyDownloads = null,
    long TotalViews = 0);

public sealed record RegistryStackViewResponse(long Views);

/// <summary>Download count for a single calendar day, used to plot the Stack downloads chart.</summary>
public sealed record RegistryStackDownloadPoint(
    DateOnly Date,
    long Count);

public sealed record RegistryStackPackageRequirement(
    string PackageId,
    string InstallTag,
    string? CreatedWithVersion,
    string? MinimumVersion,
    bool Required,
    string? Name = null,
    string? IconUrl = null);

public sealed record RegistryStackFragmentSummary(
    string FragmentId,
    string OwnerPackageId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string? Description,
    bool DefaultSelected,
    string? SourceItemId = null,
    string? Kind = null,
    IReadOnlyList<RegistryStackFragmentDetail>? DisplayDetails = null);

public sealed record RegistryStackFragmentDetail(
    string Label,
    string Value,
    string? Behavior);

public sealed record RegistryStackRequiredInput(
    string InputId,
    string Label,
    string? Description,
    bool Required);

public sealed record RegistryStackArtifact(
    string Sha256,
    long Size,
    string DownloadUrl);

public sealed record RegistryStackProfile(
    string StackId,
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RegistryStackMedia> Media,
    DateTimeOffset? UpdatedAtUtc);

public sealed record RegistryStackMedia(
    Guid MediaId,
    string FileName,
    string ContentType,
    long Size,
    string? AltText,
    int SortOrder,
    string Url);

public sealed record RegistryUpdateStackProfileRequest(
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string>? Tags);

public sealed record RegistryStackProfileOperationResponse(
    bool Success,
    string? Message,
    RegistryStackProfile? Profile,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryPublishLocalStackRequest(string StackPath);

public sealed record RegistryPublishStackResponse(
    bool Success,
    string? StackId,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryStackManagementOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryStackStarResponse(
    bool Success,
    string? Message,
    RegistryStackStats? Stats,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

public sealed record RegistryStackMaintainer(
    string UserId,
    bool IsOwner,
    DateTimeOffset AddedAtUtc,
    string? Username = null,
    string? DisplayName = null,
    string? Email = null,
    string? AvatarUrl = null);

public sealed record RegistryStackMaintainersResponse(
    string StackId,
    IReadOnlyList<RegistryStackMaintainer> Maintainers);

public sealed record RegistryAddStackMaintainerRequest(string UserId);

public sealed record RegistryStackMaintainerOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
}

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
