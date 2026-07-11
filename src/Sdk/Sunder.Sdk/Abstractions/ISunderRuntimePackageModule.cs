using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines the Runtime-hosted role of a package module.</summary>
/// <remarks>The Runtime creates one module per activation, configures services first, builds the provider, then registers contributions. Modules must not retain either registry beyond activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface ISunderRuntimePackageModule
{
    /// <summary>Registers Runtime services for the activation.</summary>
    void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    /// <summary>Registers Runtime contributions after the package service provider has been built.</summary>
    void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);
}
