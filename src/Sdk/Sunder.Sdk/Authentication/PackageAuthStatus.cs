namespace Sunder.Sdk.Authentication;

public enum PackageAuthStatusKind
{
    Unavailable = 0,
    NotConnected = 1,
    Connected = 2,
    Failed = 3,
}

public sealed record PackageAuthStatus(
    string PackageId,
    PackageAuthStatusKind Status,
    string Message,
    bool CanAuthorize,
    bool CanDisconnect);
