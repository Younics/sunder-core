using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
public interface IPackageExtensionCatalog
{
    IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint);
}
