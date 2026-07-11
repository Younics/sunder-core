using Sunder.Registry.Contracts;

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

public sealed record RuntimeRegistryPackageBatchRequest(
    string RegistryOrigin,
    IReadOnlyList<RegistryPackageChangeRequest> Packages,
    bool IncludePrerelease = false,
    bool AllowDowngrade = false,
    bool Reinstall = false);

public sealed record RuntimeRegistryUpdateRequest(
    string RegistryOrigin,
    string? PackageId = null,
    bool IncludePrerelease = false);

public sealed record RuntimeRegistryPackageChangeResult(
    bool Success,
    RuntimeRegistryErrorCode ErrorCode,
    string Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RegistryPackageInstallPlanItem> PlanItems);

public sealed record RuntimeRegistryStarRequest(string RegistryOrigin, string ResourceId, bool Starred);

public sealed record RuntimeRegistryPublishRequest(string RegistryOrigin, string UploadId, bool SetLatest = true);

public sealed record RuntimeRegistryDeleteStackRequest(string RegistryOrigin, string StackId);

public sealed record RuntimeRegistryYankRequest(string RegistryOrigin, string PackageId, string Version, bool IsYanked);

public sealed record RuntimeRegistryDeprecateRequest(string RegistryOrigin, string PackageId, string Version, string? Message);

public sealed record RuntimeRegistryDistTagRequest(string RegistryOrigin, string PackageId, string Tag, string? Version);
