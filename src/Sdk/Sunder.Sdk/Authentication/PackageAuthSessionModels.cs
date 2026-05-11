using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Authentication;

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthFlowKind
{
    Browser = 0,
}

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthSessionState
{
    Pending = 0,
    Connected = 1,
    Failed = 2,
    Cancelled = 3,
}

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStartContext(
    string AuthSessionId,
    Uri CallbackUri);

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStartResult(
    string PackageId,
    string AuthSessionId,
    PackageAuthFlowKind Flow,
    string LaunchUrl,
    string Message);

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionCompletionContext(
    string AuthSessionId,
    IReadOnlyDictionary<string, string?> QueryValues);

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStatus(
    string PackageId,
    string AuthSessionId,
    PackageAuthSessionState State,
    string Message,
    string? LaunchUrl = null);
