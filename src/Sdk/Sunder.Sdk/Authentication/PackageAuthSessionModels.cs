using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Authentication;

/// <summary>Specifies how authorization is presented to the user.</summary>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthFlowKind
{
    /// <summary>The host launches an external browser and receives a callback.</summary>
    Browser = 0,
}

/// <summary>Describes authorization-session lifecycle state.</summary>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public enum PackageAuthSessionState
{
    /// <summary>The user or provider has not completed authorization.</summary>
    Pending = 0,
    /// <summary>Credentials were accepted and stored by the package.</summary>
    Connected = 1,
    /// <summary>Authorization ended unsuccessfully.</summary>
    Failed = 2,
    /// <summary>The user or host cancelled authorization.</summary>
    Cancelled = 3,
}

/// <summary>Supplies host-created identity and callback URI when authorization starts.</summary>
/// <param name="AuthSessionId">Opaque session id that must be echoed in subsequent results.</param>
/// <param name="CallbackUri">Absolute host-owned callback URI registered with the provider.</param>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStartContext(
    string AuthSessionId,
    Uri CallbackUri);

/// <summary>Describes the user action required to continue authorization.</summary>
/// <param name="PackageId">Package that owns the session.</param>
/// <param name="AuthSessionId">Opaque id from the start context.</param>
/// <param name="Flow">Presentation mechanism.</param>
/// <param name="LaunchUrl">Absolute URL the host may open; the package owns URL construction.</param>
/// <param name="Message">User-facing instruction or status.</param>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStartResult(
    string PackageId,
    string AuthSessionId,
    PackageAuthFlowKind Flow,
    string LaunchUrl,
    string Message);

/// <summary>Supplies provider callback values for authorization completion.</summary>
/// <param name="AuthSessionId">Opaque id identifying the pending session.</param>
/// <param name="QueryValues">Read-only callback query values; keys may have null values.</param>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionCompletionContext(
    string AuthSessionId,
    IReadOnlyDictionary<string, string?> QueryValues);

/// <summary>Provides an immutable authorization-session status snapshot.</summary>
/// <param name="PackageId">Package that owns the session.</param>
/// <param name="AuthSessionId">Opaque session id.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Message">User-facing state explanation.</param>
/// <param name="LaunchUrl">Optional URL while user action remains necessary.</param>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
public sealed record PackageAuthSessionStatus(
    string PackageId,
    string AuthSessionId,
    PackageAuthSessionState State,
    string Message,
    string? LaunchUrl = null);
