using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public interface IPackageStackImportAppliedHandler
{
    ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default);
}
