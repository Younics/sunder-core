using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public static class SunderStackExtensionPoints
{
    public static readonly PackageExtensionPoint<IPackageStackContributor> StackContributors =
        new("sunder:stack-contributors");
}
