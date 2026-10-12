using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public enum RuntimeRegistryAuthSessionState
{
    Pending,
    Succeeded,
    Failed,
    Expired,
}

public enum RuntimeRegistryErrorCode
{
    None,
    InvalidRequest,
    AuthenticationRequired,
    Forbidden,
    NotFound,
    Conflict,
    RegistryUnavailable,
    DownloadTooLarge,
    ArtifactVerificationFailed,
    Cancelled,
    InternalError,
    ConsentRequired,
    StalePlan,
    RuntimePrerequisitesRequired,
}

public sealed record RuntimeRegistryUser(
    string UserId,
    string? DisplayName,
    string? Email,
    string? Username,
    string? AvatarUrl,
    bool RequiresUsername = false);

public sealed record RuntimeRegistryAuthStartRequest(
    string RegistryOrigin,
    string? AuthorizationOrigin = null,
    string? DisplayName = null);

public sealed record RuntimeRegistryAuthStartResponse(
    string SessionId,
    string RegistryOrigin,
    string LaunchUrl,
    DateTimeOffset ExpiresAtUtc);

public sealed record RuntimeRegistryAuthSessionStatus(
    string SessionId,
    string RegistryOrigin,
    RuntimeRegistryAuthSessionState State,
    RuntimeRegistryUser? User,
    DateTimeOffset? CredentialExpiresAtUtc,
    RuntimeRegistryErrorCode ErrorCode,
    string? Message);

public sealed record RuntimeRegistryAuthStatus(
    string RegistryOrigin,
    bool IsSignedIn,
    RuntimeRegistryUser? User,
    DateTimeOffset? ExpiresAtUtc,
    RuntimeRegistryErrorCode ErrorCode = RuntimeRegistryErrorCode.None,
    string? Message = null);

public sealed record RuntimeRegistryOriginRequest(string RegistryOrigin);

public sealed record RuntimeRegistryPackageRequest(
    string RegistryOrigin,
    string PackageId,
    string? Version = null,
    string? Tag = "latest",
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false,
    string? VersionRange = null,
    bool Required = true,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null,
    IReadOnlyList<PackageCapabilityConsent>? CapabilityConsents = null,
    string? ExpectedResolutionFingerprint = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisiteExpectation? RuntimePrerequisiteExpectation { get; init; }
}

public sealed record RuntimeRegistryPackageChangeRequest(
    string PackageId,
    string? Version,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest> DesiredTargets,
    string? Tag = null,
    string? VersionRange = null,
    bool Required = true);

public sealed record RuntimeRegistryPackageBatchRequest(
    string RegistryOrigin,
    IReadOnlyList<RuntimeRegistryPackageChangeRequest> Packages,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false,
    IReadOnlyList<PackageCapabilityConsent>? CapabilityConsents = null,
    string? ExpectedResolutionFingerprint = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisiteExpectation? RuntimePrerequisiteExpectation { get; init; }

    public IReadOnlyList<RuntimeRegistryPackageChangeRequest> Packages { get; }
        = Array.AsReadOnly(Packages.ToArray());
    public IReadOnlyList<PackageCapabilityConsent> CapabilityConsents { get; init; }
        = Array.AsReadOnly((CapabilityConsents ?? []).ToArray());
}

public sealed record RuntimeRegistryPackagePlanExpectation(
    string RegistryOrigin,
    bool IncludePrerelease,
    string ExpectedResolutionFingerprint);

public sealed record RuntimeRegistryUpdateRequest(
    string? RegistryOrigin = null,
    string? PackageId = null,
    bool IncludePrerelease = false,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null,
    bool DryRun = false,
    IReadOnlyList<PackageCapabilityConsent>? CapabilityConsents = null,
    IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? ExpectedPlans = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisiteExpectation? RuntimePrerequisiteExpectation { get; init; }

    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> ExpectedPlans { get; init; }
        = Array.AsReadOnly((ExpectedPlans ?? []).ToArray());
}

public sealed record RuntimeRegistrySourceAdoptionRequest(
    string PackageId,
    string RegistryOrigin,
    string? Tag = null,
    string? Version = null,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null,
    bool Confirm = false,
    bool DryRun = false,
    IReadOnlyList<PackageCapabilityConsent>? CapabilityConsents = null,
    string? ExpectedResolutionFingerprint = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisiteExpectation? RuntimePrerequisiteExpectation { get; init; }
}

public sealed record RuntimeRegistryPackageDependency(
    string PackageId,
    string VersionRange);

public sealed record RuntimeRegistryPackageTargetRequest(
    string Role,
    string Rid);

public sealed record RuntimeRegistryPackageProjectionKey(
    string Kind,
    string? Rid);

public sealed record RuntimeRegistryPackageProjectionArtifact(
    string Kind,
    string? Rid,
    string Sha256,
    long Size,
    string DownloadUrl,
    string SourceArchiveSha256,
    string ManifestSha256,
    string ProjectionContentIdentity,
    int ProjectionFormatVersion);

/// <summary>Package target metadata and requirement only; official runtime selection is local to Host.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRegistryPackageTarget(
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Rid,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string EntryPoint,
    [property: JsonRequired] string? TargetFramework,
    [property: JsonRequired] string? SdkVersion,
    IReadOnlyList<string> RequiredHostCapabilities,
    IReadOnlyList<RuntimeRegistryPackageWebView>? Views = null,
    RuntimeRegistryExecutionRuntimeRequirement? ExecutionRuntime = null)
{
    private IReadOnlyList<string> _requiredHostCapabilities = RuntimeContractCollections.FreezeBounded(RequiredHostCapabilities, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeRegistryPackageWebView> _views = RuntimeContractCollections.FreezeBounded(Views ?? [], RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired] public IReadOnlyList<string> RequiredHostCapabilities { get => _requiredHostCapabilities; init => _requiredHostCapabilities = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    public IReadOnlyList<RuntimeRegistryPackageWebView> Views { get => _views; init => _views = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
}

public sealed record RuntimeRegistryPackageWebView(
    string ViewId,
    string DisplayName,
    string Route,
    string? Icon,
    string DefaultPlacement,
    bool ShowInHotbar);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRegistryPackageInstallPlanItem(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string? CurrentVersion,
    [property: JsonRequired] string? Version,
    [property: JsonRequired] string PackageContentIdentity,
    [property: JsonRequired] string ActionKind,
    [property: JsonRequired] string? DeprecatedMessage,
    IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn,
    IReadOnlyList<RuntimeRegistryPackageTarget> Targets,
    IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts)
{
    private IReadOnlyList<RuntimeRegistryPackageDependency> _dependsOn = RuntimeContractCollections.FreezeBounded(DependsOn, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeRegistryPackageTarget> _targets = RuntimeContractCollections.FreezeBounded(Targets, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> _artifacts = RuntimeContractCollections.FreezeBounded(Artifacts, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn { get => _dependsOn; init => _dependsOn = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageTarget> Targets { get => _targets; init => _targets = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts { get => _artifacts; init => _artifacts = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRegistryExecutionRuntimeRequirement(
    [property: JsonRequired] string RuntimeId,
    [property: JsonRequired] string VersionRange,
    [property: JsonRequired] string ModuleAbi);

public sealed record RuntimeRegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string ErrorCode = "registry.v1.resource.conflict",
    string Message = "");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRegistryResolveInstallPlanResponse(
    [property: JsonRequired] bool Success,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts,
    IReadOnlyList<string> TrustedArtifactOrigins,
    IReadOnlyList<PackageCapabilityConsent>? RequiredCapabilityConsents = null,
    [property: JsonRequired] string ResolutionFingerprint = "",
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc = default)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisitePlan? RuntimePrerequisites { get; init; }

    private IReadOnlyList<RuntimeRegistryPackageCompatibilityConflict> _compatibilityConflicts = [];
    public IReadOnlyList<RuntimeRegistryPackageCompatibilityConflict> CompatibilityConflicts
    {
        get => _compatibilityConflicts;
        init => _compatibilityConflicts = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    private IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> _items = RuntimeContractCollections.FreezeBounded(Items, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _warnings = RuntimeContractCollections.FreezeBounded(Warnings, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _errors = RuntimeContractCollections.FreezeBounded(Errors, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> _conflicts = RuntimeContractCollections.FreezeBounded(Conflicts, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _trustedArtifactOrigins = RuntimeContractCollections.FreezeBounded(TrustedArtifactOrigins, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<PackageCapabilityConsent> _requiredCapabilityConsents = RuntimeContractCollections.FreezeBounded(RequiredCapabilityConsents ?? [], RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get => _items; init => _items = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<string> Warnings { get => _warnings; init => _warnings = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<string> Errors { get => _errors; init => _errors = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get => _conflicts; init => _conflicts = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<string> TrustedArtifactOrigins { get => _trustedArtifactOrigins; init => _trustedArtifactOrigins = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get => _requiredCapabilityConsents; init => _requiredCapabilityConsents = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
}

public sealed record RuntimeRegistryPackageCompatibilityRequirement(string PackageId, string VersionRange, bool IncludePrerelease,
    string Presence, IReadOnlyList<RuntimeRpcContractIdentity> Contracts, string? RequiredByPackageId)
{
    public IReadOnlyList<RuntimeRpcContractIdentity> Contracts { get; } = RuntimeContractCollections.Freeze(Contracts);
}

public sealed record RuntimeRegistryPackageCompatibilityConflict(string PackageId, string ErrorCode,
    IReadOnlyList<RuntimeRegistryPackageCompatibilityRequirement> Requirements)
{
    public IReadOnlyList<RuntimeRegistryPackageCompatibilityRequirement> Requirements { get; } = RuntimeContractCollections.Freeze(Requirements);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRegistryPackageChangeResult(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] RuntimeRegistryErrorCode ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] bool RuntimeSessionApplied,
    [property: JsonRequired] bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems,
    IReadOnlyList<PackageCapabilityConsent>? RequiredCapabilityConsents = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackageRuntimePrerequisitePlan? RuntimePrerequisites { get; init; }

    private IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> _conflicts
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryPackageInstallPlanConflict>());
    private IReadOnlyList<RuntimeRegistryPackagePlanExpectation> _planExpectations
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryPackagePlanExpectation>());
    private IReadOnlyList<string> _skippedPackageIds = Array.AsReadOnly(Array.Empty<string>());

    private IReadOnlyList<string> _warnings = RuntimeContractCollections.FreezeBounded(Warnings, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _errors = RuntimeContractCollections.FreezeBounded(Errors, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _impactedPackageIds = RuntimeContractCollections.FreezeBounded(ImpactedPackageIds, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> _planItems = RuntimeContractCollections.FreezeBounded(PlanItems, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<PackageCapabilityConsent> _requiredCapabilityConsents = RuntimeContractCollections.FreezeBounded(RequiredCapabilityConsents ?? [], RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired] public IReadOnlyList<string> Warnings { get => _warnings; init => _warnings = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<string> Errors { get => _errors; init => _errors = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<string> ImpactedPackageIds { get => _impactedPackageIds; init => _impactedPackageIds = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    [JsonRequired] public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems { get => _planItems; init => _planItems = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }

    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts
    {
        get => _conflicts;
        init => _conflicts = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }

    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get => _requiredCapabilityConsents; init => _requiredCapabilityConsents = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems); }
    public RuntimePackageStamp? CommittedStamp { get; init; }

    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> PlanExpectations
    {
        get => _planExpectations;
        init => _planExpectations = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }

    public IReadOnlyList<string> SkippedPackageIds
    {
        get => _skippedPackageIds;
        init => _skippedPackageIds = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

public sealed record RuntimeRegistryStarRequest(string RegistryOrigin, string ResourceId, bool Starred);

public sealed record RuntimeRegistryPublishRequest(string RegistryOrigin, string UploadId, bool SetLatest = true);

public sealed record RuntimeRegistryStackVisibilityRequest(string RegistryOrigin, string StackId, bool Listed = false);

public sealed record RuntimeRegistryYankRequest(string RegistryOrigin, string PackageId, string Version, bool IsYanked);

public sealed record RuntimeRegistryDeprecateRequest(string RegistryOrigin, string PackageId, string Version, string? Message);

public sealed record RuntimeRegistryDistTagRequest(string RegistryOrigin, string PackageId, string Tag, string? Version);
