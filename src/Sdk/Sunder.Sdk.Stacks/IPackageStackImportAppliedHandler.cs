using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Receives notification after a Stack import has committed successfully.</summary>
/// <remarks>The handler may update derived package state but does not own context collections. Cancellation indicates host shutdown and should stop optional follow-up work.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public interface IPackageStackImportAppliedHandler
{
    /// <summary>Performs post-import synchronization for the committed result.</summary>
    ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default);
}
