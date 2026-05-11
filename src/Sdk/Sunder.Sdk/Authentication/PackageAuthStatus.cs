using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Authentication;

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthStatusKind
{
    Unavailable = 0,
    NotConnected = 1,
    Connected = 2,
    Failed = 3,
}

[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthStatus(
    string PackageId,
    PackageAuthStatusKind Status,
    string Message,
    bool CanAuthorize,
    bool CanDisconnect);
