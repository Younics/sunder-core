using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public interface IPackageExtensionCatalogMonitor
{
    event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;
}
