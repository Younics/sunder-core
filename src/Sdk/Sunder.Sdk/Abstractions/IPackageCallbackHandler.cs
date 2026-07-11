using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Owns one category of host-routed package callback flow.</summary>
/// <remarks>Handlers may be called concurrently. Cancellation stops work where safe; callback values remain owned by the host.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public interface IPackageCallbackHandler
{
    /// <summary>Gets the stable package-local handler id used for callback routing.</summary>
    string CallbackHandlerId { get; }

    /// <summary>Starts a flow, returning <see langword="null"/> when no flow can currently start.</summary>
    Task<PackageCallbackStartResult?> StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Validates and applies values for a pending callback session.</summary>
    Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken = default);
}
