namespace Sunder.Protocol;

public enum PackageAuthFlowKind
{
    Browser = 0,
}

public enum PackageAuthSessionState
{
    Pending = 0,
    Connected = 1,
    Failed = 2,
    Cancelled = 3,
}

public sealed record PackageAuthSessionStartResponse(
    string PackageId,
    string AuthSessionId,
    PackageAuthFlowKind Flow,
    string LaunchUrl,
    string Message);

public sealed record PackageAuthSessionStatusResponse(
    string PackageId,
    string AuthSessionId,
    PackageAuthSessionState State,
    string Message,
    string? LaunchUrl);
