namespace Sunder.Sdk.Authentication;

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

public sealed record PackageAuthSessionStartContext(
    string AuthSessionId,
    Uri CallbackUri);

public sealed record PackageAuthSessionStartResult(
    string PackageId,
    string AuthSessionId,
    PackageAuthFlowKind Flow,
    string LaunchUrl,
    string Message);

public sealed record PackageAuthSessionCompletionContext(
    string AuthSessionId,
    IReadOnlyDictionary<string, string?> QueryValues);

public sealed record PackageAuthSessionStatus(
    string PackageId,
    string AuthSessionId,
    PackageAuthSessionState State,
    string Message,
    string? LaunchUrl = null);
