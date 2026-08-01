using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Template.App.PackageViews;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Template.App;

public sealed class PackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<DefaultPackageViewModel>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<DefaultPackageView>(new PackageViewRegistration(
            "SUNDER_PACKAGE_ID_CSHARP.default",
            "SUNDER_PACKAGE_NAME_CSHARP",
            "assets/icon.png"));
    }

}
