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
    bool Reinstall = false);

public sealed record RuntimeRegistryPackageChangeRequest(
    string PackageId,
    string? Version,
    string? Tag = null);

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
    string RegistryOrigin,
    string? PackageId = null,
    bool IncludePrerelease = false);

public sealed record RuntimeRegistryPackageDependency(
    string PackageId,
    string VersionRange);

public sealed record RuntimeRegistryPackageArtifact(
    string Sha256,
    long? Size,
    string DownloadUrl);

public sealed record RuntimeRegistryPackageCompatibility(
    int SdkApiVersion,
    string SdkPackageVersion,
    IReadOnlyList<string> RequiredCapabilities,
    string? TargetFramework,
    int ManifestFormatVersion,
    int ArchiveFormatVersion)
{
    public IReadOnlyList<string> RequiredCapabilities { get; }
        = Array.AsReadOnly(RequiredCapabilities.ToArray());
}

public sealed record RuntimeRegistryPackageInstallPlanItem(
    string PackageId,
    string? CurrentVersion,
    string Version,
    bool IsUpdate,
    string? DeprecatedMessage,
    IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn,
    RuntimeRegistryPackageArtifact Artifact,
    RuntimeRegistryPackageCompatibility? Compatibility = null)
{
    public IReadOnlyList<RuntimeRegistryPackageDependency> DependsOn { get; }
        = Array.AsReadOnly(DependsOn.ToArray());
}

public sealed record RuntimeRegistryPackageInstallPlanConflict(
    string PackageId,
    string? CurrentVersion,
    string? RequestedVersionRange,
    string? RequiredByPackageId,
    string Message);

public sealed record RuntimeRegistryResolveInstallPlanResponse(
    bool Success,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts)
{
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get; }
        = Array.AsReadOnly(Items.ToArray());
    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; }
        = Array.AsReadOnly(Conflicts.ToArray());
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
    public IReadOnlyList<string> Warnings { get; } = Array.AsReadOnly(Warnings.ToArray());
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(Errors.ToArray());
    public IReadOnlyList<string> ImpactedPackageIds { get; }
        = Array.AsReadOnly(ImpactedPackageIds.ToArray());
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems { get; }
        = Array.AsReadOnly(PlanItems.ToArray());
    public RuntimePackageStamp? CommittedStamp { get; init; }
}

public sealed record RuntimeRegistryStarRequest(string RegistryOrigin, string ResourceId, bool Starred);

public sealed record RuntimeRegistryPublishRequest(string RegistryOrigin, string UploadId, bool SetLatest = true);

public sealed record RuntimeRegistryDeleteStackRequest(string RegistryOrigin, string StackId);

public sealed record RuntimeRegistryYankRequest(string RegistryOrigin, string PackageId, string Version, bool IsYanked);

public sealed record RuntimeRegistryDeprecateRequest(string RegistryOrigin, string PackageId, string Version, string? Message);

public sealed record RuntimeRegistryDistTagRequest(string RegistryOrigin, string PackageId, string Tag, string? Version);
