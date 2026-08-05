using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

[assembly: SunderPackage(Id = "test.missing.sdk.dependency", Name = "Missing SDK Dependency Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.MissingSdkDependency;

public sealed class FixturePackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
    }
}

public static class StackRpcClientReference
{
    public static StackContributorRpcClient CreateClient(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint)
        => new(scope, endpoint);
}
