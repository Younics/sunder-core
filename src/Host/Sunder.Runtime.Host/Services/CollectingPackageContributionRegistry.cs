using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;

namespace Sunder.Runtime.Host.Services;

internal sealed class CollectingPackageContributionRegistry(
    IServiceProvider serviceProvider,
    RuntimePackageExtensionCatalog extensionCatalog,
    string packageId) : IPackageContributionRegistry
{
    private readonly List<IPackageBackgroundService> _backgroundServices = [];
    private readonly List<PackageViewRegistration> _packageViews = [];

    public bool HasRegisteredViews { get; private set; }

    public bool HasRegisteredExtensions { get; private set; }

    public bool HasRegisteredBackgroundServices { get; private set; }

    public IReadOnlyList<IPackageBackgroundService> BackgroundServices => _backgroundServices;

    public IReadOnlyList<PackageViewRegistration> PackageViews => _packageViews;

    public PackageConfigurationSchema? ConfigurationSchema { get; private set; }

    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Avalonia.Controls.Control
    {
        HasRegisteredViews = true;
        _packageViews.Add(registration);
    }

    public void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory
    {
        HasRegisteredViews = true;
        _packageViews.Add(registration);
    }

    public void RegisterSettingsView<TView>() where TView : Avalonia.Controls.Control
    {
    }

    public void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory
    {
    }

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
