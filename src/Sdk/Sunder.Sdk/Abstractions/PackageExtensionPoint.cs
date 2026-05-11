using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public sealed record PackageExtensionPoint<TContribution>(string Id)
{
    public override string ToString() => Id;
}
