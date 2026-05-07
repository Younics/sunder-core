using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed partial class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new HeadlessPackageRuntime(context.PackageId, context.Version.ToString()));
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        RegisterHostContractContributions(registry, services);
    }

    partial void RegisterHostContractContributions(IPackageContributionRegistry registry, IServiceProvider services);

    private sealed record HeadlessPackageRuntime(string PackageId, string PackageVersion);
}
