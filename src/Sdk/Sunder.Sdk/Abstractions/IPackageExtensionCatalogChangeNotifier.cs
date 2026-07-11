using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides coarse invalidation when active extension availability changes.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionChangesV1)]
public interface IPackageExtensionCatalogChangeNotifier
{
    /// <summary>Occurs after any catalog mutation; handlers should reacquire snapshots and return promptly.</summary>
    event EventHandler? ExtensionsChanged;
}
