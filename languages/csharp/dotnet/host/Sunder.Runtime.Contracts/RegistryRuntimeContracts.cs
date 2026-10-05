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
    string? ExpectedResolutionFingerprint = null);

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
    string? ExpectedResolutionFingerprint = null);

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

public sealed record RuntimeRegistryPackageTarget(
    string Role,
    string Rid,
    string Kind,
    string EntryPoint,
    string? TargetFramework,
    string? SdkVersion,
    IReadOnlyList<string> RequiredHostCapabilities,
    IReadOnlyList<RuntimeRegistryPackageWebView>? Views = null,
    RuntimeRegistryExecutionRuntimeRequirement? ExecutionRuntime = null)
{
    public IReadOnlyList<string> RequiredHostCapabilities { get; }
        = Array.AsReadOnly(RequiredHostCapabilities.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageWebView> Views { get; }
        = Array.AsReadOnly((Views ?? []).ToArray());
}

public sealed record RuntimeRegistryPackageWebView(
    string ViewId,
    string DisplayName,
    string Route,
    string? Icon,
    string DefaultPlacement,
    bool ShowInHotbar);

public sealed record RuntimeRegistryPackageInstallPlanItem(
    string PackageId,
    string? CurrentVersion,
    string? Version,
    string PackageContentIdentity,
    string ActionKind,
    string? DeprecatedMessage,
    IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn,
    IReadOnlyList<RuntimeRegistryPackageTarget> Targets,
    IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts,
    IReadOnlyList<RuntimeRegistryPackageTargetRuntimeBinding>? RuntimeBindings = null,
    RuntimeRegistryRuntimeOnlyRebindDetails? RuntimeOnlyRebind = null)
{
    public IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn { get; }
        = Array.AsReadOnly(DependsOn.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageTarget> Targets { get; }
        = Array.AsReadOnly(Targets.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts { get; }
        = Array.AsReadOnly(Artifacts.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageTargetRuntimeBinding> RuntimeBindings { get; }
        = Array.AsReadOnly((RuntimeBindings ?? []).ToArray());
}

public sealed record RuntimeRegistryExecutionRuntimeRequirement(
    string RuntimeId,
    string VersionRange,
    string ModuleAbi);

public sealed record RuntimeRegistryExecutionRuntimeIdentity(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi);

public sealed record RuntimeRegistryExecutionRuntimeLifecycle(
    string State,
    DateTimeOffset StagedAtUtc,
    DateTimeOffset? SupportedFromUtc,
    DateTimeOffset? DeprecatedAtUtc,
    DateTimeOffset? InstallBlockedAtUtc,
    DateTimeOffset? ActivationBlockedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? Message);

public sealed record RuntimeRegistryResolvedExecutionRuntimeArtifact(
    RuntimeRegistryExecutionRuntimeIdentity Identity,
    string ManifestSha256,
    string ArtifactSha256,
    long ArtifactSize,
    string SigningKeyId,
    long MinimumHostTrustGeneration,
    string NativeAttestationSha256,
    string NativeAttestationKeyId,
    RuntimeRegistryExecutionRuntimeLifecycle Lifecycle,
    long ObservedRuntimePolicyRevision,
    string DownloadUrl);

public sealed record RuntimeRegistryPackageTargetRuntimeBinding(
    string Role,
    string Rid,
    RuntimeRegistryExecutionRuntimeRequirement Requirement,
    RuntimeRegistryExecutionRuntimeIdentity Runtime);

public sealed record RuntimeRegistryRuntimeOnlyRebindDetails(
    string PackageVersion,
    string PackageContentIdentity,
    IReadOnlyList<RuntimeRegistryPackageTargetRuntimeBinding> PreviousBindings,
    IReadOnlyList<RuntimeRegistryPackageTargetRuntimeBinding> NewBindings);

public sealed record RuntimeRegistryExecutionRuntimeResolutionConflict(
    string Code,
    string Message,
    RuntimeRegistryExecutionRuntimeRequirement Requirement,
    string Rid,
    string? PackageId = null,
    string? TargetRole = null,
    RuntimeRegistryExecutionRuntimeIdentity? CurrentRuntime = null,
    long? RequiredHostTrustGeneration = null);

public sealed record RuntimeRegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string ErrorCode = "registry.v1.resource.conflict",
    string Message = "");

public sealed record RuntimeRegistryResolveInstallPlanResponse(
    bool Success,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts,
    IReadOnlyList<string> TrustedArtifactOrigins,
    IReadOnlyList<PackageCapabilityConsent>? RequiredCapabilityConsents = null,
    IReadOnlyList<RuntimeRegistryResolvedExecutionRuntimeArtifact>? RuntimeArtifacts = null,
    IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict>? RuntimeConflicts = null,
    string ResolutionFingerprint = "",
    long RuntimePolicyRevision = 0,
    DateTimeOffset ExpiresAtUtc = default)
{
    private IReadOnlyList<RuntimeRegistryPackageCompatibilityConflict> _compatibilityConflicts = [];
    public IReadOnlyList<RuntimeRegistryPackageCompatibilityConflict> CompatibilityConflicts
    {
        get => _compatibilityConflicts;
        init => _compatibilityConflicts = RuntimeContractCollections.Freeze(value);
    }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get; }
        = Array.AsReadOnly(Items.ToArray());
    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; }
        = Array.AsReadOnly(Conflicts.ToArray());
    public IReadOnlyList<string> TrustedArtifactOrigins { get; }
        = Array.AsReadOnly(TrustedArtifactOrigins.ToArray());
    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; init; }
        = Array.AsReadOnly((RequiredCapabilityConsents ?? []).ToArray());
    public IReadOnlyList<RuntimeRegistryResolvedExecutionRuntimeArtifact> RuntimeArtifacts { get; }
        = Array.AsReadOnly((RuntimeArtifacts ?? []).ToArray());
    public IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> RuntimeConflicts { get; }
        = Array.AsReadOnly((RuntimeConflicts ?? []).ToArray());
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

public sealed record RuntimeRegistryPackageChangeResult(
    bool Success,
    RuntimeRegistryErrorCode ErrorCode,
    string Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems,
    IReadOnlyList<PackageCapabilityConsent>? RequiredCapabilityConsents = null)
{
    private IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> _conflicts
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryPackageInstallPlanConflict>());
    private IReadOnlyList<RuntimeRegistryPackagePlanExpectation> _planExpectations
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryPackagePlanExpectation>());
    private IReadOnlyList<RuntimeRegistryResolvedExecutionRuntimeArtifact> _runtimeArtifacts
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryResolvedExecutionRuntimeArtifact>());
    private IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> _runtimeConflicts
        = Array.AsReadOnly(Array.Empty<RuntimeRegistryExecutionRuntimeResolutionConflict>());
    private IReadOnlyList<string> _skippedPackageIds = Array.AsReadOnly(Array.Empty<string>());

    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<string> ImpactedPackageIds { get; }
        = Array.AsReadOnly(ImpactedPackageIds.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems { get; }
        = Array.AsReadOnly(PlanItems.ToArray());

    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts
    {
        get => _conflicts;
        init => _conflicts = Array.AsReadOnly((value ?? []).ToArray());
    }

    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; init; }
        = Array.AsReadOnly((RequiredCapabilityConsents ?? []).ToArray());
    public RuntimePackageStamp? CommittedStamp { get; init; }

    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> PlanExpectations
    {
        get => _planExpectations;
        init => _planExpectations = Array.AsReadOnly((value ?? []).ToArray());
    }

    public IReadOnlyList<RuntimeRegistryResolvedExecutionRuntimeArtifact> RuntimeArtifacts
    {
        get => _runtimeArtifacts;
        init => _runtimeArtifacts = Array.AsReadOnly((value ?? []).ToArray());
    }

    public IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> RuntimeConflicts
    {
        get => _runtimeConflicts;
        init => _runtimeConflicts = Array.AsReadOnly((value ?? []).ToArray());
    }

    public IReadOnlyList<string> SkippedPackageIds
    {
        get => _skippedPackageIds;
        init => _skippedPackageIds = Array.AsReadOnly((value ?? []).ToArray());
    }
}

public sealed record RuntimeRegistryStarRequest(string RegistryOrigin, string ResourceId, bool Starred);

public sealed record RuntimeRegistryPublishRequest(string RegistryOrigin, string UploadId, bool SetLatest = true);

public sealed record RuntimeRegistryStackVisibilityRequest(string RegistryOrigin, string StackId, bool Listed = false);

public sealed record RuntimeRegistryYankRequest(string RegistryOrigin, string PackageId, string Version, bool IsYanked);

public sealed record RuntimeRegistryDeprecateRequest(string RegistryOrigin, string PackageId, string Version, string? Message);

public sealed record RuntimeRegistryDistTagRequest(string RegistryOrigin, string PackageId, string Tag, string? Version);
