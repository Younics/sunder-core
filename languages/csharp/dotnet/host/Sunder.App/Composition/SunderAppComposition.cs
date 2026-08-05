using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Composition;

internal static class SunderAppComposition
{
    public static ServiceProvider CreateServiceProvider(
        Application application,
        AppStartupOptions startupOptions,
        AppPackageResourceAssemblyRegistry packageResourceAssemblyRegistry)
    {
        var services = new ServiceCollection();

        services.AddSingleton(application);
        services.AddSingleton(startupOptions);
        services.AddSingleton(packageResourceAssemblyRegistry);
        services.AddSingleton<IUiDispatcher>(AvaloniaUiDispatcher.Instance);
        services.AddSingleton<SunderAppSettings>(_ => SunderAppSettings.Load());

        services.AddSingleton<ShellStateService>();
        services.AddSingleton(provider => provider.GetRequiredService<ShellStateService>().Load());
        services.AddSingleton(provider => new RuntimeConnectionState(provider.GetRequiredService<AppStartupOptions>().RuntimeUrl));
        services.AddSingleton(provider =>
        {
            var transport = new RuntimeClientTransport(
                provider.GetRequiredService<RuntimeConnectionState>().GetConnectionInfo);
            PackageIconImageLoader.ConfigureRuntimeTransport(transport);
            return transport;
        });

        services.AddSingleton<RuntimeApiClientFactory>();
        services.AddSingleton<IRuntimeApiClientFactory>(provider => provider.GetRequiredService<RuntimeApiClientFactory>());
        services.AddSingleton<RuntimeHostProcessManager>();
        services.AddSingleton<DevPackageOwnerSession>();
        services.AddSingleton<NotificationCenterService>();
        services.AddSingleton<IPackageNotificationService>(provider => new AppPackageNotificationService(
            provider.GetRequiredService<NotificationCenterService>(),
            "sunder.app",
            "Sunder"));
        services.AddSingleton<DeveloperLogService>();
        services.AddSingleton<RuntimeEventSubscriptionServiceFactory>();
        services.AddSingleton<CliInstallationService>();
        services.AddSingleton<AppUpdateSettingsService>();
        services.AddSingleton<SunderUpdateService>();
        services.AddSingleton<BackgroundProcessQueueService>();
        services.AddSingleton<IBackgroundProcessQueue>(provider => provider.GetRequiredService<BackgroundProcessQueueService>());
        services.AddSingleton<RegistryPackageInstallService>();
        services.AddSingleton<LocalStackLibraryService>();
        services.AddSingleton<ExternalBrowserService>();
        services.AddSingleton<RegistryAuthService>();
        services.AddSingleton(provider => new PackageUpdateStartupCheckService(
            provider.GetRequiredService<IBackgroundProcessQueue>(),
            provider.GetRequiredService<IRuntimeApiClientFactory>(),
            provider.GetRequiredService<IPackageNotificationService>()));
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
        services.AddSingleton<StacksWindowFactory>();
        services.AddSingleton<StackWizardWindowFactory>();
        services.AddSingleton<ShellStartupCoordinator>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
