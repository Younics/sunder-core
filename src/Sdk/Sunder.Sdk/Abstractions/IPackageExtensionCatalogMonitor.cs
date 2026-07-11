using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides revisioned contribution-level extension catalog changes.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public interface IPackageExtensionCatalogMonitor
{
    /// <summary>Occurs after an atomic catalog revision; handlers should return promptly.</summary>
    event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;
}
