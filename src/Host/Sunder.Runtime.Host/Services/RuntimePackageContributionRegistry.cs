using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageContributionRegistry(
    IServiceProvider serviceProvider,
    RuntimePackageExtensionCatalog extensionCatalog,
    string packageId) : ISunderRuntimeContributionRegistry
{
    private readonly List<IPackageBackgroundService> _backgroundServices = [];

    public bool HasRegisteredExtensions { get; private set; }

    public bool HasRegisteredBackgroundServices { get; private set; }

    public IReadOnlyList<IPackageBackgroundService> BackgroundServices => _backgroundServices;

    public PackageConfigurationSchema? ConfigurationSchema { get; private set; }

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
    {
        HasRegisteredBackgroundServices = true;
        _backgroundServices.Add(serviceProvider.GetRequiredService<TService>());
    }

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        HasRegisteredExtensions = true;
        extensionCatalog.Add(packageId, extensionPoint, contribution);
    }

    public void RegisterConfigurationSchema(PackageConfigurationSchema schema)
    {
        ConfigurationSchema = schema;
    }
}
