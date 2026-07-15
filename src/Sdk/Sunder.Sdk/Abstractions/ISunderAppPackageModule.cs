using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines the desktop App-hosted role of a package module.</summary>
/// <remarks>The App constructs the module through its public parameterless constructor, calls <see cref="ConfigureAppServices"/>, builds the role-specific provider, then calls <see cref="RegisterAppContributions"/>. The module is not resolved from that provider. App and Runtime roles have separate module instances, providers, and lifecycles even when one type implements both roles.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface ISunderAppPackageModule
{
    /// <summary>Registers App-side services for the activation.</summary>
    void ConfigureAppServices(IServiceCollection services, IPackageContext context);

    /// <summary>Registers App contributions after the package service provider has been built.</summary>
    void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services);
}
