using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Composition;

internal static class SunderAppComposition
{
    public static ServiceProvider CreateServiceProvider(Application application, AppStartupOptions startupOptions)
    {
        var services = new ServiceCollection();

        services.AddSingleton(application);
        services.AddSingleton(startupOptions);
        services.AddSingleton<SunderAppSettings>(_ => SunderAppSettings.Load());

        services.AddSingleton<ShellStateService>();
        services.AddSingleton(provider => provider.GetRequiredService<ShellStateService>().Load());
        services.AddSingleton(provider => new RuntimeConnectionState(provider.GetRequiredService<AppStartupOptions>().RuntimeUrl));

        services.AddSingleton<RuntimeApiClientFactory>();
        services.AddSingleton<IRuntimeApiClientFactory>(provider => provider.GetRequiredService<RuntimeApiClientFactory>());
        services.AddSingleton<RuntimeHostProcessManager>();
        services.AddSingleton<NotificationCenterService>();
        services.AddSingleton<CliInstallationService>();
        services.AddSingleton<AppUpdateSettingsService>();
        services.AddSingleton<SunderUpdateService>();
        services.AddSingleton<BackgroundProcessQueueService>();
        services.AddSingleton<RegistryPackageInstallService>();
        services.AddSingleton<PackageRuntimeFaultReporter>();

        services.AddSingleton<AppPackageShellViewService>();
        services.AddSingleton<IPackageShellViewService>(provider => provider.GetRequiredService<AppPackageShellViewService>());
        services.AddSingleton<AppPackageSettingsNavigationService>();
        services.AddSingleton<IPackageSettingsNavigationService>(provider => provider.GetRequiredService<AppPackageSettingsNavigationService>());

        services.AddSingleton<ThemeManager>();
        services.AddSingleton<IThemeManager>(provider => provider.GetRequiredService<ThemeManager>());
        services.AddSingleton<IShellCompositionService, ShellCompositionService>();

        services.AddSingleton<PackageViewHostServiceFactory>();
        services.AddSingleton<WindowLauncherFactory>();
        services.AddSingleton<MainWindowFactory>();
        services.AddSingleton<SettingsWindowFactory>();
        services.AddSingleton<PackagesWindowFactory>();
        services.AddSingleton<ShellStartupCoordinator>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
