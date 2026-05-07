using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;

namespace Sunder.App.Services;

internal sealed class AppPackageContributionRegistry(
    IServiceProvider serviceProvider,
    AppPackageViewRegistry viewRegistry,
    AppPackageBackgroundServiceCoordinator backgroundServices,
    AppPackageExtensionCatalog extensionCatalog,
    string packageId) : IPackageContributionRegistry
{
    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
    {
        viewRegistry.RegisterPackageView<TView>(packageId, registration.Id, serviceProvider);
    }

    public void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory
    {
        viewRegistry.RegisterPackageViewFactory<TFactory>(packageId, registration.Id, serviceProvider);
    }

    public void RegisterSettingsView<TView>() where TView : Control
    {
        viewRegistry.RegisterSettingsView<TView>(packageId, serviceProvider);
    }

    public void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory
    {
        viewRegistry.RegisterSettingsViewFactory<TFactory>(packageId, serviceProvider);
    }

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
    {
        backgroundServices.Register(packageId, serviceProvider.GetRequiredService<TService>());
    }

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        extensionCatalog.Add(packageId, extensionPoint, contribution);
    }

    public void RegisterConfigurationSchema(PackageConfigurationSchema schema)
    {
    }
}
