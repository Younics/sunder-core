using System.Collections.ObjectModel;
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
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed class PackageCallbackStartContext
{
    /// <summary>Creates a callback start context and takes an immutable snapshot of bounded parameters.</summary>
    public PackageCallbackStartContext(
        string callbackSessionId,
        Uri callbackUri,
        string? purpose = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackSessionId);
        ArgumentNullException.ThrowIfNull(callbackUri);
        if (!callbackUri.IsAbsoluteUri) throw new ArgumentException("The callback URI must be absolute.", nameof(callbackUri));
        CallbackSessionId = callbackSessionId;
        CallbackUri = callbackUri;
        Purpose = purpose;
        Parameters = PackageCallbackParameters.CopyAndValidate(parameters);
    }

    /// <summary>Gets the opaque host-created session id.</summary>
    public string CallbackSessionId { get; }
    /// <summary>Gets the absolute host-owned callback URI for this session.</summary>
    public Uri CallbackUri { get; }
    /// <summary>Gets the optional package-defined diagnostic purpose.</summary>
    public string? Purpose { get; }
    /// <summary>Gets the immutable schema-free parameters supplied by App package code.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }
}

/// <summary>Defines V1 bounds for generic package callback parameters.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public static class PackageCallbackParameters
{
    /// <summary>Maximum number of parameters in one callback start request.</summary>
    public const int MaximumCount = 16;
    /// <summary>Maximum parameter key length.</summary>
    public const int MaximumKeyLength = 64;
    /// <summary>Maximum parameter value length.</summary>
    public const int MaximumValueLength = 2048;
    /// <summary>Maximum combined key and value length.</summary>
    public const int MaximumTotalLength = 8192;

    /// <summary>Validates parameters and returns an immutable ordinal snapshot.</summary>
    public static IReadOnlyDictionary<string, string> CopyAndValidate(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }
        if (parameters.Count > MaximumCount)
        {
            throw new ArgumentException($"Callback parameters cannot contain more than {MaximumCount} entries.", nameof(parameters));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var pair in parameters)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > MaximumKeyLength)
            {
                throw new ArgumentException($"Callback parameter keys must contain 1 to {MaximumKeyLength} characters.", nameof(parameters));
            }
            if (pair.Value is null || pair.Value.Length > MaximumValueLength)
            {
                throw new ArgumentException($"Callback parameter values cannot exceed {MaximumValueLength} characters.", nameof(parameters));
            }
            totalLength = checked(totalLength + pair.Key.Length + pair.Value.Length);
            if (totalLength > MaximumTotalLength)
            {
                throw new ArgumentException($"Callback parameters cannot exceed {MaximumTotalLength} total characters.", nameof(parameters));
            }
            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Callback parameter keys must be unique.", nameof(parameters));
            }
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}

/// <summary>Describes why a pending callback session is being cancelled.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public enum PackageCallbackCancellationReason
{
    /// <summary>The callback session expired.</summary>
    Expired = 0,
    /// <summary>The package activation was unloaded or replaced.</summary>
    PackageUnloaded = 1,
    /// <summary>The Runtime is shutting down.</summary>
    HostShutdown = 2,
}

/// <summary>Supplies cancellation details for a pending callback session.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackCancellationContext(
    string CallbackSessionId,
    PackageCallbackCancellationReason Reason);

/// <summary>Describes generic callback-session lifecycle state exposed to App package code.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public enum PackageCallbackSessionState
{
    /// <summary>The callback is awaiting user or provider action.</summary>
    Pending = 0,
    /// <summary>The callback and its follow-up work completed.</summary>
    Completed = 1,
    /// <summary>The callback flow failed.</summary>
    Failed = 2,
    /// <summary>The callback flow was cancelled.</summary>
    Cancelled = 3,
    /// <summary>The callback session expired.</summary>
    Expired = 4,
}

/// <summary>Provides an immutable generic callback-session status snapshot.</summary>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackSessionStatus(
    string PackageId,
    string CallbackHandlerId,
    string CallbackSessionId,
    PackageCallbackSessionState State,
    string Message,
    Uri? LaunchUri,
    DateTimeOffset ExpiresAtUtc);

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
