using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Collects desktop App contributions during one package activation.</summary>
/// <remarks>The App owns registrations and contribution instances until deactivation. Registration is single-threaded and valid only during module activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface ISunderAppContributionRegistry
{
    /// <summary>Registers a host-owned contribution instance for a typed extension point.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
    void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution);
}
