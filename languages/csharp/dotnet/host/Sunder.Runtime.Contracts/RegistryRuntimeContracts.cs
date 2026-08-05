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
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null);

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
    bool Reinstall = false)
{
    public IReadOnlyList<RuntimeRegistryPackageChangeRequest> Packages { get; }
        = Array.AsReadOnly(Packages.ToArray());
}

public sealed record RuntimeRegistryUpdateRequest(
    string? RegistryOrigin = null,
    string? PackageId = null,
    bool IncludePrerelease = false,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null);

public sealed record RuntimeRegistrySourceAdoptionRequest(
    string PackageId,
    string RegistryOrigin,
    string? Tag = null,
    string? Version = null,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    IReadOnlyList<RuntimeRegistryPackageTargetRequest>? DesiredTargets = null,
    bool Confirm = false,
    bool DryRun = false);

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
    IReadOnlyList<RuntimeRegistryPackageWebView>? Views = null)
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
    string Version,
    bool IsUpdate,
    string? DeprecatedMessage,
    IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn,
    IReadOnlyList<RuntimeRegistryPackageTarget> Targets,
    IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts)
{
    public IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn { get; }
        = Array.AsReadOnly(DependsOn.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageTarget> Targets { get; }
        = Array.AsReadOnly(Targets.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageProjectionArtifact> Artifacts { get; }
        = Array.AsReadOnly(Artifacts.ToArray());
}

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
    IReadOnlyList<string> TrustedArtifactOrigins)
{
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get; }
        = Array.AsReadOnly(Items.ToArray());
    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; }
        = Array.AsReadOnly(Conflicts.ToArray());
    public IReadOnlyList<string> TrustedArtifactOrigins { get; }
        = Array.AsReadOnly(TrustedArtifactOrigins.ToArray());
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
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems)
{
    private IReadOnlyList<string> _skippedPackageIds = Array.AsReadOnly(Array.Empty<string>());

    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<string> ImpactedPackageIds { get; }
        = Array.AsReadOnly(ImpactedPackageIds.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems { get; }
        = Array.AsReadOnly(PlanItems.ToArray());
    public RuntimePackageStamp? CommittedStamp { get; init; }

    public IReadOnlyList<string> SkippedPackageIds
    {
        get => _skippedPackageIds;
        init => _skippedPackageIds = Array.AsReadOnly((value ?? []).ToArray());
    }
}

public sealed record RuntimeRegistryStarRequest(string RegistryOrigin, string ResourceId, bool Starred);

public sealed record RuntimeRegistryPublishRequest(string RegistryOrigin, string UploadId, bool SetLatest = true);

public sealed record RuntimeRegistryDeleteStackRequest(string RegistryOrigin, string StackId);

public sealed record RuntimeRegistryYankRequest(string RegistryOrigin, string PackageId, string Version, bool IsYanked);

public sealed record RuntimeRegistryDeprecateRequest(string RegistryOrigin, string PackageId, string Version, string? Message);

public sealed record RuntimeRegistryDistTagRequest(string RegistryOrigin, string PackageId, string Tag, string? Version);
