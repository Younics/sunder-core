using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Defines standard typed extension points used by Stack hosts.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public static class SunderStackExtensionPoints
{
    /// <summary>Gets the extension point for activation-scoped Stack contributors.</summary>
    public static readonly PackageExtensionPoint<IPackageStackContributor> StackContributors =
        new("sunder:stack-contributors");
}
