using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines the desktop App-hosted role of a package module.</summary>
/// <remarks>The App creates one module per activation, configures services first, builds the provider, then registers contributions. App and Runtime roles have separate providers and lifecycles.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface ISunderAppPackageModule
{
    /// <summary>Registers App-side services for the activation.</summary>
    void ConfigureAppServices(IServiceCollection services, IPackageContext context);

    /// <summary>Registers App contributions after the package service provider has been built.</summary>
    void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services);
}
