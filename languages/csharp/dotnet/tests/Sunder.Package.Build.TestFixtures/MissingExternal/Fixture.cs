using Sunder.Package.Build.Tests.Fixtures.ExternalDependency;
using Sunder.Sdk.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

[assembly: SunderPackage(Id = "test.missing.external", Name = "Missing External Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.MissingExternal;

public sealed class ExternalConsumer : ExternalBase
{
    public string CallExternal(string value) => ExternalApi.Normalize(value);
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
