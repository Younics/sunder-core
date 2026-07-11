using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Authentication;

/// <summary>Describes the package's current authorization capability and connection state.</summary>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthStatusKind
{
    /// <summary>Authorization is not supported or cannot currently be determined.</summary>
    Unavailable = 0,
    /// <summary>No usable authorization credentials are stored.</summary>
    NotConnected = 1,
    /// <summary>Usable authorization credentials are available.</summary>
    Connected = 2,
    /// <summary>The latest status or authorization operation failed.</summary>
    Failed = 3,
}

/// <summary>Provides an immutable authorization status snapshot.</summary>
/// <param name="PackageId">Package that owns the credentials.</param>
/// <param name="Status">Current connection state.</param>
/// <param name="Message">User-facing state explanation.</param>
/// <param name="CanAuthorize">Whether the host should offer an authorization action.</param>
/// <param name="CanDisconnect">Whether the host should offer credential removal.</param>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthStatus(
    string PackageId,
    PackageAuthStatusKind Status,
    string Message,
    bool CanAuthorize,
    bool CanDisconnect);
