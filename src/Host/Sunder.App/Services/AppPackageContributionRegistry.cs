using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.App.Services;

internal sealed class AppPackageContributionRegistry(
    IServiceProvider serviceProvider,
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
    string packageId,
    PackageExtensionOwnerActivation? extensionOwner = null) : IAvaloniaPackageContributionRegistry
{
    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
    {
        viewRegistry.RegisterPackageView<TView>(packageId, registration, serviceProvider);
    }

    public void RegisterSettingsView<TView>() where TView : Control
    {
        viewRegistry.RegisterSettingsView<TView>(packageId, serviceProvider);
    }

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        if (extensionOwner is null)
        {
            extensionCatalog.Add(packageId, extensionPoint, contribution);
        }
        else
        {
            extensionCatalog.Add(extensionOwner, extensionPoint, contribution);
        }
    }

}
