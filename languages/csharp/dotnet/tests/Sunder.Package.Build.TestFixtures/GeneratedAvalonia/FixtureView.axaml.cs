using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "test.generated.avalonia", Name = "Generated Avalonia Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.GeneratedAvalonia;

public sealed partial class FixtureView : UserControl
{
    public FixtureView() => InitializeComponent();
}

public sealed record FixtureRecord(string Value);

public sealed class FixturePackageModule : ISunderAppPackageModule, Sunder.App.Bridge.IAppPackageEntryPointV1
{
    public ValueTask<Sunder.App.Bridge.IAppPackageActivationV1> CreateAsync(
        Sunder.App.Bridge.IAppPackageHostV1 host, Sunder.App.Bridge.IAppContributionSinkV1 contributions)
        => Sunder.Sdk.Avalonia.Hosting.AppPackageRuntime.CreateAsync(this, host, contributions);

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}
