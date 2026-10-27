using System.Text.Json.Serialization;

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
    IReadOnlyList<string> TrustedArtifactOrigins)
{
    /// <summary>Selected immutable-version details; null when supplied by an older Registry.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPackageInformation? Information { get; init; }

    /// <summary>Verified original publication policy; absent evidence never confirms a previous upload.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPackagePublicationEvidence? Publication { get; init; }
}

public sealed record RegistryPackageDependency(
    string PackageId,
    string VersionRange);

public sealed record RegistryPackageCanonicalArtifact(
    string Sha256,
    long Size,
    string DownloadUrl);

/// <summary>Requirements stay attached to their immutable package role/RID target; runtime selection is Host-owned.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageTarget(
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Rid,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string EntryPoint,
    [property: JsonRequired] string? TargetFramework,
    [property: JsonRequired] string? SdkVersion,
    IReadOnlyList<string> RequiredHostCapabilities,
    IReadOnlyList<RegistryPackageWebView>? Views = null,
    RegistryExecutionRuntimeRequirement? ExecutionRuntime = null)
{
    private IReadOnlyList<string> _requiredHostCapabilities = PolicyCollections.FreezeBounded(RequiredHostCapabilities);
    private IReadOnlyList<RegistryPackageWebView> _views = PolicyCollections.FreezeBounded(Views ?? []);
    [JsonRequired] public IReadOnlyList<string> RequiredHostCapabilities { get => _requiredHostCapabilities; init => _requiredHostCapabilities = PolicyCollections.FreezeBounded(value); }
    public IReadOnlyList<RegistryPackageWebView> Views { get => _views; init => _views = PolicyCollections.FreezeBounded(value); }
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
    string DescriptorSha256,
    string AbiSha256,
    string Exposure);

public sealed record RegistryPackageContractUse(
    string Role,
    string ContractId,
    string ContractVersion,
    string AbiSha256,
    bool Required,
    IReadOnlyList<string> Actions);

public sealed record RegistryPackageProvider(
    string ProviderId,
    string Role,
    string ContractId,
    string ContractVersion,
    string DescriptorSha256,
    string AbiSha256,
    string Audience);

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryResolveUpdatesRequest(
    IReadOnlyList<RegistryInstalledPackage> InstalledPackages,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    [property: System.Text.Json.Serialization.JsonRequired]
    RegistryClientCompatibilityProfile Compatibility,
    string? ExpectedResolutionFingerprint = null,
    bool IncludePrerelease = false)
{
    private IReadOnlyList<RegistryInstalledPackage> _installedPackages = PolicyCollections.FreezeBounded(InstalledPackages);
    private IReadOnlyList<RegistryPackageTargetRequest> _desiredTargets = PolicyCollections.FreezeBounded(DesiredTargets);
    [JsonRequired] public IReadOnlyList<RegistryInstalledPackage> InstalledPackages { get => _installedPackages; init => _installedPackages = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets { get => _desiredTargets; init => _desiredTargets = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryInstalledPackage(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Version,
    IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections,
    [property: JsonRequired] string PackageContentIdentity)
{
    private IReadOnlyList<RegistryPackageProjectionKey> _acquiredProjections = PolicyCollections.FreezeBounded(AcquiredProjections);
    [JsonRequired] public IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections { get => _acquiredProjections; init => _acquiredProjections = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryResolveUpdatesResponse(
    IReadOnlyList<RegistryPackageUpdate> Updates,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> TrustedArtifactOrigins,
    IReadOnlyList<RegistryPackageInstallPlanItem>? Actions = null,
    [property: JsonRequired] string ResolutionFingerprint = "",
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc = default)
{
    /// <summary>Verified package policy for these actions; absence is not authorization.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPolicyIdentity? PackagePolicy { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPackageResolutionPolicy? ResolutionPolicy { get; init; }
    private IReadOnlyList<RegistryPackageCompatibilityConflict> _compatibilityConflicts = [];
    public IReadOnlyList<RegistryPackageCompatibilityConflict> CompatibilityConflicts { get => _compatibilityConflicts; init => _compatibilityConflicts = PolicyCollections.FreezeBounded(value); }
    private IReadOnlyList<RegistryPackageUpdate> _updates = PolicyCollections.FreezeBounded(Updates);
    private IReadOnlyList<string> _errors = PolicyCollections.FreezeBounded(Errors);
    private IReadOnlyList<string> _trustedArtifactOrigins = PolicyCollections.FreezeBounded(TrustedArtifactOrigins);
    private IReadOnlyList<RegistryPackageInstallPlanItem> _actions = PolicyCollections.FreezeBounded(Actions ?? []);
    [JsonRequired] public IReadOnlyList<RegistryPackageUpdate> Updates { get => _updates; init => _updates = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<string> Errors { get => _errors; init => _errors = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<string> TrustedArtifactOrigins { get => _trustedArtifactOrigins; init => _trustedArtifactOrigins = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageInstallPlanItem> Actions { get => _actions; init => _actions = PolicyCollections.FreezeBounded(value); }
}

public sealed record RegistryPackageUpdate(
    string PackageId,
    string CurrentVersion,
    string AvailableVersion,
    string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryResolveInstallPlanRequest(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string? Version,
    [property: JsonRequired] string? Tag,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    IReadOnlyList<RegistryInstalledPackageState> InstalledPackages,
    [property: System.Text.Json.Serialization.JsonRequired]
    RegistryClientCompatibilityProfile Compatibility,
    string? ExpectedResolutionFingerprint = null,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false,
    string? VersionRange = null,
    bool Required = true)
{
    private IReadOnlyList<RegistryPackageTargetRequest> _desiredTargets = PolicyCollections.FreezeBounded(DesiredTargets);
    private IReadOnlyList<RegistryInstalledPackageState> _installedPackages = PolicyCollections.FreezeBounded(InstalledPackages);
    [JsonRequired] public IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets { get => _desiredTargets; init => _desiredTargets = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryInstalledPackageState> InstalledPackages { get => _installedPackages; init => _installedPackages = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageChangeRequest(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string? Version,
    IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets,
    string? Tag = null,
    string? VersionRange = null,
    bool Required = true)
{
    private IReadOnlyList<RegistryPackageTargetRequest> _desiredTargets = PolicyCollections.FreezeBounded(DesiredTargets);
    [JsonRequired] public IReadOnlyList<RegistryPackageTargetRequest> DesiredTargets { get => _desiredTargets; init => _desiredTargets = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryResolvePackageChangesRequest(
    IReadOnlyList<RegistryPackageChangeRequest> Packages,
    IReadOnlyList<RegistryInstalledPackageState> InstalledPackages,
    [property: System.Text.Json.Serialization.JsonRequired]
    RegistryClientCompatibilityProfile Compatibility,
    string? ExpectedResolutionFingerprint = null,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false)
{
    private IReadOnlyList<RegistryPackageChangeRequest> _packages = PolicyCollections.FreezeBounded(Packages);
    private IReadOnlyList<RegistryInstalledPackageState> _installedPackages = PolicyCollections.FreezeBounded(InstalledPackages);
    [JsonRequired] public IReadOnlyList<RegistryPackageChangeRequest> Packages { get => _packages; init => _packages = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryInstalledPackageState> InstalledPackages { get => _installedPackages; init => _installedPackages = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryInstalledPackageState(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Version,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections,
    [property: JsonRequired] string PackageContentIdentity)
{
    private IReadOnlyList<RegistryPackageDependency> _dependsOn = PolicyCollections.FreezeBounded(DependsOn);
    private IReadOnlyList<RegistryPackageProjectionKey> _acquiredProjections = PolicyCollections.FreezeBounded(AcquiredProjections);
    [JsonRequired] public IReadOnlyList<RegistryPackageDependency> DependsOn { get => _dependsOn; init => _dependsOn = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageProjectionKey> AcquiredProjections { get => _acquiredProjections; init => _acquiredProjections = PolicyCollections.FreezeBounded(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageInstallPlanItem(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string? CurrentVersion,
    [property: JsonRequired] string? Version,
    [property: JsonRequired] string PackageContentIdentity,
    [property: JsonRequired] string ActionKind,
    [property: JsonRequired] string? DeprecatedMessage,
    IReadOnlyList<RegistryPackageDependency> DependsOn,
    IReadOnlyList<RegistryPackageTarget> Targets,
    IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts)
{
    private IReadOnlyList<RegistryPackageDependency> _dependsOn = PolicyCollections.FreezeBounded(DependsOn);
    private IReadOnlyList<RegistryPackageTarget> _targets = PolicyCollections.FreezeBounded(Targets);
    private IReadOnlyList<RegistryPackageProjectionArtifact> _artifacts = PolicyCollections.FreezeBounded(Artifacts);
    [JsonRequired] public IReadOnlyList<RegistryPackageDependency> DependsOn { get => _dependsOn; init => _dependsOn = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageTarget> Targets { get => _targets; init => _targets = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageProjectionArtifact> Artifacts { get => _artifacts; init => _artifacts = PolicyCollections.FreezeBounded(value); }
}

public sealed record RegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string ErrorCode = RegistryV1ErrorCodes.Conflict,
    string Message = "");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryResolveInstallPlanResponse(
    [property: JsonRequired] bool Success,
    IReadOnlyList<RegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RegistryPackageInstallPlanConflict> Conflicts,
    IReadOnlyList<string> TrustedArtifactOrigins,
    [property: JsonRequired] string ResolutionFingerprint = "",
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc = default)
{
    /// <summary>Verified package policy for this plan; absence is not authorization.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPolicyIdentity? PackagePolicy { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryPackageResolutionPolicy? ResolutionPolicy { get; init; }
    private IReadOnlyList<RegistryPackageCompatibilityConflict> _compatibilityConflicts = [];
    public IReadOnlyList<RegistryPackageCompatibilityConflict> CompatibilityConflicts { get => _compatibilityConflicts; init => _compatibilityConflicts = PolicyCollections.FreezeBounded(value); }
    private IReadOnlyList<RegistryPackageInstallPlanItem> _items = PolicyCollections.FreezeBounded(Items);
    private IReadOnlyList<string> _warnings = PolicyCollections.FreezeBounded(Warnings);
    private IReadOnlyList<string> _errors = PolicyCollections.FreezeBounded(Errors);
    private IReadOnlyList<RegistryPackageInstallPlanConflict> _conflicts = PolicyCollections.FreezeBounded(Conflicts);
    private IReadOnlyList<string> _trustedArtifactOrigins = PolicyCollections.FreezeBounded(TrustedArtifactOrigins);
    [JsonRequired] public IReadOnlyList<RegistryPackageInstallPlanItem> Items { get => _items; init => _items = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<string> Warnings { get => _warnings; init => _warnings = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<string> Errors { get => _errors; init => _errors = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageInstallPlanConflict> Conflicts { get => _conflicts; init => _conflicts = PolicyCollections.FreezeBounded(value); }
    [JsonRequired] public IReadOnlyList<string> TrustedArtifactOrigins { get => _trustedArtifactOrigins; init => _trustedArtifactOrigins = PolicyCollections.FreezeBounded(value); }
}
