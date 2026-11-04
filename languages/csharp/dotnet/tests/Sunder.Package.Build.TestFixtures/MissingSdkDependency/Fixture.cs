using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

[assembly: SunderPackage(Id = "test.missing.sdk.dependency", Name = "Missing SDK Dependency Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.MissingSdkDependency;

public sealed class FixturePackageModule : ISunderAppPackageModule, Sunder.App.Bridge.IAppPackageEntryPointV1
{
    public ValueTask<Sunder.App.Bridge.IAppPackageActivationV1> CreateAsync(
        Sunder.App.Bridge.IAppPackageHostV1 host, Sunder.App.Bridge.IAppContributionSinkV1 contributions)
        => throw new NotSupportedException("Metadata-only fixture.");

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}

public static class StackDomainAdapterReference
{
    public static StackContributorServiceAdapter CreateAdapter(IPackageStackContributor contributor)
        => new(contributor);
}
