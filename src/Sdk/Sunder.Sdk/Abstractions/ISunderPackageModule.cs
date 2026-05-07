using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Sdk.Abstractions;

public interface ISunderPackageModule
{
    void ConfigureServices(IServiceCollection services, IPackageContext context);

    void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services);
}
