using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines the Runtime-hosted role of a package module.</summary>
/// <remarks>The Runtime constructs the module through its public parameterless constructor, calls <see cref="ConfigureRuntimeServices"/>, builds the role-specific provider, then calls <see cref="RegisterRuntimeContributions"/>. The module is not resolved from that provider and must not retain the registry beyond activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface ISunderRuntimePackageModule
{
    /// <summary>Registers Runtime services for the activation.</summary>
    void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    /// <summary>Registers Runtime contributions after the package service provider has been built.</summary>
    void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);
}
