using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Template.PackageViews;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Template;

public sealed partial class PackageModule : ISunderRuntimePackageModule, ISunderAppPackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new PackageRuntimeState(context.Storage.State));
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<DefaultPackageViewModel>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        RegisterHostContractContributions(registry, services);
        RegisterStackContributions(registry);
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<DefaultPackageView>(new PackageViewRegistration(
            "sunder.package.template.default",
            "Sunder Package Template"));
    }

    partial void RegisterHostContractContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);

    partial void RegisterStackContributions(ISunderRuntimeContributionRegistry registry);
}
