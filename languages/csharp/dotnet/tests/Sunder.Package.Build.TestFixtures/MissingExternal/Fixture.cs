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
