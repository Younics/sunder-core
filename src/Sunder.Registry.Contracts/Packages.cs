namespace Sunder.Registry.Contracts;

public enum RegistrySearchSort
{
    Updated,
    Downloads,
    Stars,
}

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
    IReadOnlyList<RegistryUserAttribution>? Maintainers = null,
    int? TotalVersionCount = null,
    int? TotalDependentCount = null);

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
    bool IsYanked,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    RegistryPackageCanonicalArtifact CanonicalArtifact,
    IReadOnlyList<RegistryPackageTarget> Targets,
    IReadOnlyList<RegistryPackageProjectionArtifact> Projections,
    IReadOnlyList<RegistryPackageContractBundle> ContractBundles,
    IReadOnlyList<RegistryPackageContractUse> UsesContracts,
    IReadOnlyList<RegistryPackageProvider> Providers,
    int ManifestFormatVersion,
    int ArchiveFormatVersion,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<string> TrustedArtifactOrigins);

public sealed record RegistryPackageDependency(
    string PackageId,
    string VersionRange);

public sealed record RegistryPackageCanonicalArtifact(
    string Sha256,
    long Size,
    string DownloadUrl);

public sealed record RegistryPackageTarget(
    string Role,
    string Rid,
    string Kind,
    string EntryPoint,
    string? TargetFramework,
    string? SdkVersion,
    IReadOnlyList<string> RequiredHostCapabilities,
    IReadOnlyList<RegistryPackageWebView>? Views = null)
{
    public IReadOnlyList<RegistryPackageWebView> Views { get; }
        = Array.AsReadOnly((Views ?? []).ToArray());
}

public sealed record RegistryPackageWebView(
    string ViewId,
    string DisplayName,
    string Route,
    string? Icon,
    string DefaultPlacement,
    bool ShowInHotbar);

public sealed record RegistryPackageTargetRequest(
    string Role,
    string Rid);

public sealed record RegistryPackageProjectionKey(
    string Kind,
    string? Rid);

public sealed record RegistryPackageProjectionArtifact(
    string Kind,
    string? Rid,
    string Sha256,
    long Size,
    string DownloadUrl,
    string SourceArchiveSha256,
    string ManifestSha256,
    string ProjectionContentIdentity,
    int ProjectionFormatVersion);

public sealed record RegistryPackageContractBundle(
    string ContractId,
    string Version,
    string DescriptorPath,
    string Sha256);

public sealed record RegistryPackageContractUse(
    string ContractId,
    string VersionRange,
    bool Required,
    IReadOnlyList<string> Actions);

public sealed record RegistryPackageProvider(
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string ContractSha256,
    string Role);

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
    public bool NotFound { get; init; }
}

public sealed record RegistryPackageResolveResponse(
    string PackageId,
    string Version,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts,
    IReadOnlyList<string> TrustedArtifactOrigins);

public sealed record RegistryResolveUpdatesRequest(
    IReadOnlyList<RegistryInstalledPackage> InstalledPackages,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    bool IncludePrerelease = false);

public sealed record RegistryInstalledPackage(
    string PackageId,
    string Version,
    IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections);

public sealed record RegistryResolveUpdatesResponse(
    IReadOnlyList<RegistryPackageUpdate> Updates,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> TrustedArtifactOrigins);

public sealed record RegistryPackageUpdate(
    string PackageId,
    string CurrentVersion,
    string AvailableVersion,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts);

public sealed record RegistryResolveInstallPlanRequest(
    string PackageId,
    string? Version,
    string? Tag,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    IReadOnlyList<RegistryInstalledPackageState> InstalledPackages,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false,
    string? VersionRange = null,
    bool Required = true);

public sealed record RegistryPackageChangeRequest(
    string PackageId,
    string? Version,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    string? Tag = null,
    string? VersionRange = null,
    bool Required = true);

public sealed record RegistryResolvePackageChangesRequest(
    IReadOnlyList<RegistryPackageChangeRequest> Packages,
    IReadOnlyList<RegistryInstalledPackageState> InstalledPackages,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false);

public sealed record RegistryInstalledPackageState(
    string PackageId,
    string Version,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections);

public sealed record RegistryPackageInstallPlanItem(
    string PackageId,
    string? CurrentVersion,
    string Version,
    bool IsUpdate,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    IReadOnlyList<RegistryPackageTarget> Targets,
    IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts);

public sealed record RegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string ErrorCode = RegistryV1ErrorCodes.Conflict,
    string Message = "");

public sealed record RegistryResolveInstallPlanResponse(
    bool Success,
    IReadOnlyList<RegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RegistryPackageInstallPlanConflict> Conflicts,
    IReadOnlyList<string> TrustedArtifactOrigins);
