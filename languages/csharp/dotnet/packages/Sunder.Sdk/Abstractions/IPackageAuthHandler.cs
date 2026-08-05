using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Implements package-owned authorization lifecycle and generic browser callbacks.</summary>
/// <remarks>Handlers are activation-scoped and may be called concurrently. They own credential validation and persistence through package secrets.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public interface IPackageAuthHandler : IPackageCallbackHandler
{
    string IPackageCallbackHandler.CallbackHandlerId => PackageCallbackHandlerIds.Authentication;

    /// <summary>Gets current authorization status without starting user interaction.</summary>
    ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts authorization, returning <see langword="null"/> when no flow can currently start.</summary>
    Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken cancellationToken = default);

    /// <summary>Validates callback values, persists resulting credentials, and returns current status.</summary>
    Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken cancellationToken = default);

    /// <summary>Removes package-owned credentials and returns resulting status.</summary>
    Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default);

    async Task<PackageCallbackStartResult?> IPackageCallbackHandler.StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken)
    {
        var result = await StartAuthorizationAsync(
            new PackageAuthSessionStartContext(context.CallbackSessionId, context.CallbackUri),
            cancellationToken).ConfigureAwait(false);
        return result is null
            ? null
            : new PackageCallbackStartResult(
                result.PackageId,
                result.AuthSessionId,
                result.Flow switch
                {
                    PackageAuthFlowKind.Browser => PackageCallbackFlowKind.Browser,
                    _ => PackageCallbackFlowKind.Browser,
                },
                result.LaunchUrl,
                result.Message);
    }

    async Task<PackageCallbackCompletionResult> IPackageCallbackHandler.CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken)
    {
        var status = await CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(context.CallbackSessionId, context.QueryValues),
            cancellationToken).ConfigureAwait(false);
        return new PackageCallbackCompletionResult(
            status.PackageId,
            context.CallbackSessionId,
            status.Status switch
            {
                PackageAuthStatusKind.Connected => PackageCallbackCompletionState.Completed,
                _ => PackageCallbackCompletionState.Failed,
            },
            status.Message);
    }
}
