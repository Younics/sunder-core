using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimePackageUploadRetentionLimits
{
    public const int RenewalSeconds = 30 * 60;
    public const int MaximumLifetimeSeconds = 2 * 60 * 60;
    public const int MaximumLeases = 32;
}

public static class RuntimePackageUploadRetentionErrorCodes
{
    public const string Unavailable = "upload.retention-unavailable";
    public const string Conflict = "upload.retention-conflict";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageUploadRetentionRequest(
    [property: JsonRequired] string UploadId,
    [property: JsonRequired] string ExpectedContentHash,
    [property: JsonRequired] long ExpectedLength);

/// <summary>Opaque proof for one immutable upload in this authenticated user's current Host instance.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageUploadRetentionHandle(
    [property: JsonRequired] string UploadId,
    [property: JsonRequired] string LeaseId,
    [property: JsonRequired] string ContentHash,
    [property: JsonRequired] long Length);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageUploadRetention(
    RuntimePackageUploadRetentionHandle Handle,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset MaximumExpiresAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageUploadRetentionResult(
    bool Success,
    string? ErrorCode,
    string Message,
    RuntimePackageUploadRetention? Retention);

public static partial class RuntimeApiRoutes
{
    public const string PackageUploadRetention = Root + "/uploads/packages/retention";
    public const string PackageUploadRetentionRenew = PackageUploadRetention + "/renew";
    public const string PackageUploadRetentionStatus = PackageUploadRetention + "/status";
    public const string PackageUploadRetentionRelease = PackageUploadRetention + "/release";
}
