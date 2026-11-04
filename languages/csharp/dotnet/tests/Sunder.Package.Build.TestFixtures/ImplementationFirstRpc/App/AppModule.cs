using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Example.Generated.App;

public sealed class AppModule : ISunderAppPackageModule, Sunder.App.Bridge.IAppPackageEntryPointV1
{
    public ValueTask<Sunder.App.Bridge.IAppPackageActivationV1> CreateAsync(
        Sunder.App.Bridge.IAppPackageHostV1 host, Sunder.App.Bridge.IAppContributionSinkV1 contributions)
        => Sunder.Sdk.Avalonia.Hosting.AppPackageRuntime.CreateAsync(this, host, contributions);

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}
