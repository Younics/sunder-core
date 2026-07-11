using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed partial class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new PackageRuntimeState(context.Storage.State));
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        RegisterHostContractContributions(registry, services);
        RegisterStackContributions(registry);
    }

    partial void RegisterHostContractContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);

    partial void RegisterStackContributions(ISunderRuntimeContributionRegistry registry);
}
