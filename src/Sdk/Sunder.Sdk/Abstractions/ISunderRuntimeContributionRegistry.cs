using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Configuration;

namespace Sunder.Sdk.Abstractions;

/// <summary>Collects Runtime contributions during one package activation.</summary>
/// <remarks>The Runtime owns registrations and their service instances until deactivation. Registration is single-threaded and valid only during module activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface ISunderRuntimeContributionRegistry
{
    /// <summary>Registers an activation-scoped service that the host starts and stops.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
    void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService;

    /// <summary>Registers a host-owned contribution instance for a typed extension point.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
    void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution);

    /// <summary>Registers the package's complete host-rendered configuration schema.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
    void RegisterConfigurationSchema(PackageConfigurationSchema schema);
}
