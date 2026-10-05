using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Example.Generated.App;

public sealed class AppModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}
