using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.Runtime;

public sealed partial class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new PackageRuntimeState(context.Storage.State));
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        RegisterStackContributions(registry, services);
    }

    partial void RegisterStackContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);
}
