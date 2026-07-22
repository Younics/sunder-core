using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Quickstart;

public sealed class QuickstartRuntimeModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
        => services.AddSingleton<GreetHandler>();

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
    {
        registry.RegisterRuntimeOperation(
            QuickstartOperations.Greet,
            services.GetRequiredService<GreetHandler>());

        registry.RegisterSettingsSchema(new PackageSettingsSchema(
            summary: "Quickstart settings.",
            sections:
            [
                new PackageSettingsSection(
                    sectionId: "greeting",
                    title: "Greeting",
                    description: "Defaults used by the quickstart view.",
                    fields:
                    [
                        new PackageSettingsField(
                            key: "default-name",
                            label: "Default name",
                            kind: PackageSettingsFieldKind.Text,
                            defaultValue: "Sunder"),
                    ]),
            ]));
    }
}

public sealed class QuickstartAppModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterPackageView<QuickstartView>(new PackageViewRegistration(
            id: "docs.sunder.quickstart.main",
            name: "Sunder Quickstart",
            defaultPlacement: PackageViewPlacement.Middle,
            showInHotbarByDefault: true));
}
