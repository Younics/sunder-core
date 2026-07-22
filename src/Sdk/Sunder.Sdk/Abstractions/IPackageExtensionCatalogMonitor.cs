using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides revisioned contribution-level extension catalog changes.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public interface IPackageExtensionCatalogMonitor
{
    /// <summary>Occurs after an atomic catalog revision; handlers should return promptly.</summary>
    /// <remarks>The host isolates exceptions thrown by one handler so remaining subscribers still receive the revision.</remarks>
    event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;
}
