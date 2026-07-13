namespace Sunder.Runtime.Contracts;

public enum PackageCallbackSessionState
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Expired = 4,
}

public sealed record PackageCallbackSessionStartRequest(
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record PackageCallbackSessionResponse(
    string PackageId,
    string CallbackHandlerId,
    string CallbackSessionId,
    PackageCallbackSessionState State,
    string Message,
    string? LaunchUri,
    DateTimeOffset ExpiresAtUtc);
