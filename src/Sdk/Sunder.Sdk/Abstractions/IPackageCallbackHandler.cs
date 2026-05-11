using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
public interface IPackageCallbackHandler
{
    string CallbackHandlerId { get; }

    Task<PackageCallbackStartResult?> StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken = default);

    Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken = default);
}
