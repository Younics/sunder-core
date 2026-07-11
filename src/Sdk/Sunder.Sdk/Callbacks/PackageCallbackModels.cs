using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Callbacks;

/// <summary>Describes the terminal outcome of a generic callback session.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public enum PackageCallbackCompletionState
{
    /// <summary>The callback was validated and applied.</summary>
    Completed = 0,
    /// <summary>The callback was received but could not be applied.</summary>
    Failed = 1,
    /// <summary>The callback flow was cancelled before completion.</summary>
    Cancelled = 2,
}

/// <summary>Supplies host-created identity and callback URI when a callback flow starts.</summary>
/// <param name="CallbackSessionId">Opaque id that must be echoed by the handler.</param>
/// <param name="CallbackUri">Absolute host-owned callback URI.</param>
/// <param name="Purpose">Optional package-defined diagnostic purpose.</param>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackStartContext(
    string CallbackSessionId,
    Uri CallbackUri,
    string? Purpose = null);

/// <summary>Describes the user action required to continue a callback flow.</summary>
/// <param name="PackageId">Package that owns the flow.</param>
/// <param name="CallbackSessionId">Opaque id from the start context.</param>
/// <param name="Flow">Presentation mechanism.</param>
/// <param name="LaunchUrl">Absolute URL the host may open.</param>
/// <param name="Message">User-facing instruction or status.</param>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackStartResult(
    string PackageId,
    string CallbackSessionId,
    PackageCallbackFlowKind Flow,
    string LaunchUrl,
    string Message);

/// <summary>Supplies query values received by the host callback endpoint.</summary>
/// <param name="CallbackSessionId">Opaque id identifying the pending flow.</param>
/// <param name="QueryValues">Read-only callback values; individual values may be <see langword="null"/>.</param>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackCompletionContext(
    string CallbackSessionId,
    IReadOnlyDictionary<string, string?> QueryValues);

/// <summary>Reports the terminal callback result to the host.</summary>
/// <param name="PackageId">Package that handled the callback.</param>
/// <param name="CallbackSessionId">Opaque completed-session id.</param>
/// <param name="State">Terminal outcome.</param>
/// <param name="Message">User-facing outcome explanation.</param>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackCompletionResult(
    string PackageId,
    string CallbackSessionId,
    PackageCallbackCompletionState State,
    string Message);
