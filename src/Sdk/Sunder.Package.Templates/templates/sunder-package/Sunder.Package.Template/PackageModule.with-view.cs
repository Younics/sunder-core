using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Template.PackageViews;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed partial class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new PackageRuntimeState(context.PackageId, context.Version.ToString()));
        services.AddTransient<DefaultPackageViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<DefaultPackageView>(new PackageViewRegistration(
            "sunder.package.template.default",
            "Sunder Package Template"));
        RegisterHostContractContributions(registry, services);
    }

    partial void RegisterHostContractContributions(IPackageContributionRegistry registry, IServiceProvider services);

    private sealed record PackageRuntimeState(string PackageId, string PackageVersion);
}
