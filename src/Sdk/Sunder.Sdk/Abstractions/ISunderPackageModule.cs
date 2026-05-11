using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface ISunderPackageModule
{
    void ConfigureServices(IServiceCollection services, IPackageContext context);

    void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services);
}
