using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Quickstart.App;

public sealed class PackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterPackageView<QuickstartView>(new PackageViewRegistration(
            id: "docs.sunder.quickstart.main",
            name: "Sunder Quickstart",
            defaultPlacement: PackageViewPlacement.Middle,
            showInHotbarByDefault: true));
}
