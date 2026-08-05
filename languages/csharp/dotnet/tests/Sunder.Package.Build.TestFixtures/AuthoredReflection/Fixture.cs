using Sunder.Sdk.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

[assembly: SunderPackage(Id = "test.authored.reflection", Name = "Authored Reflection Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.AuthoredReflection;

public static class AuthoredSdkReflection
{
    public static Type? ResolveSdkContract()
        => Type.GetType("Sunder.Sdk.Abstractions.IPackageContext, Sunder.Sdk");
}

public sealed class FixturePackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
    }
}
