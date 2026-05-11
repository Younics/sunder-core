using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Callbacks;

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public enum PackageCallbackCompletionState
{
    Completed = 0,
    Failed = 1,
    Cancelled = 2,
}

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackStartContext(
    string CallbackSessionId,
    Uri CallbackUri,
    string? Purpose = null);

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackStartResult(
    string PackageId,
    string CallbackSessionId,
    PackageCallbackFlowKind Flow,
    string LaunchUrl,
    string Message);

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackCompletionContext(
    string CallbackSessionId,
    IReadOnlyDictionary<string, string?> QueryValues);

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public sealed record PackageCallbackCompletionResult(
    string PackageId,
    string CallbackSessionId,
    PackageCallbackCompletionState State,
    string Message);
