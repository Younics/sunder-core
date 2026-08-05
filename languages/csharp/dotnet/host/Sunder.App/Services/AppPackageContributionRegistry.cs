using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.App.Services;

internal sealed class AppPackageContributionRegistry(
    IServiceProvider serviceProvider,
    AppPackageViewRegistry viewRegistry,
    string packageId) : IAvaloniaPackageContributionRegistry
{
    public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
    {
        viewRegistry.RegisterPackageView<TView>(packageId, registration, serviceProvider);
    }

    public void RegisterSettingsView<TView>() where TView : Control
    {
        viewRegistry.RegisterSettingsView<TView>(packageId, serviceProvider);
    }

}
