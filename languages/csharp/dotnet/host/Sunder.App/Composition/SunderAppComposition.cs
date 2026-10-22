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
        => CreateServices(application, startupOptions, packageResourceAssemblyRegistry)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    internal static ServiceCollection CreateServices(
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
        services.AddSingleton<RuntimeAvailabilityManager>();
        services.AddSingleton<Sunder.App.Services.DevPackageOwnerSession>();
        services.AddSingleton<NotificationCenterService>();
        services.AddSingleton<IPackageNotificationService>(provider => new AppPackageNotificationService(
            provider.GetRequiredService<NotificationCenterService>(),
            "sunder.app",
            "Sunder"));
        services.AddSingleton<DeveloperLogService>();
        services.AddSingleton<RemoteImageService>();
        services.AddSingleton<RuntimePackageRecoveryNotificationService>();
        services.AddSingleton<RuntimeEventSubscriptionServiceFactory>();
        services.AddSingleton<CliInstallationService>();
        services.AddSingleton<AppUpdateSettingsService>();
        services.AddSingleton<AppRuntimeResourcePolicySettingsService>();
        services.AddSingleton<IRuntimeResourcePolicyStatusSource, RuntimeResourcePolicyStatusSource>();
        services.AddSingleton<SunderUpdateService>();
        services.AddSingleton<BackgroundTaskQueueService>();
        services.AddSingleton<IBackgroundTaskQueue>(provider => provider.GetRequiredService<BackgroundTaskQueueService>());
        services.AddSingleton<RegistryPackageInstallService>();
        services.AddSingleton<LocalStackLibraryService>();
        services.AddSingleton<ExternalBrowserService>();
        services.AddSingleton<RegistryAuthService>();
        services.AddSingleton(provider => new PackageUpdateStartupCheckService(
            provider.GetRequiredService<IBackgroundTaskQueue>(),
            provider.GetRequiredService<IRuntimeApiClientFactory>(),
            provider.GetRequiredService<IPackageNotificationService>()));
        services.AddSingleton<AppPackageShellViewService>();
        services.AddSingleton<IPackageShellViewService>(provider => provider.GetRequiredService<AppPackageShellViewService>());
        services.AddSingleton<AppPackageSettingsNavigationService>();
        services.AddSingleton<AppPackageDeveloperServices>();
        services.AddSingleton<IPackageSettingsNavigationService>(provider => provider.GetRequiredService<AppPackageSettingsNavigationService>());

        services.AddSingleton<ThemeManager>();
        services.AddSingleton<IThemeManager>(provider => provider.GetRequiredService<ThemeManager>());
        services.AddSingleton<IShellCompositionService, ShellCompositionService>();

        services.AddSingleton(provider => new AppRuntimeProcessTransport(provider.GetRequiredService<RuntimeClientTransport>()));
        services.AddSingleton(provider => new AppProcessPolicySupervisor(provider.GetRequiredService<AppRuntimeProcessTransport>(), () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (application is App app) app.RequestPolicyShutdown();
            })));
        services.AddSingleton(provider => new PackageViewHostServiceFactory(
            provider.GetRequiredService<AppPackageShellViewService>(), provider.GetRequiredService<AppPackageSettingsNavigationService>(),
            provider.GetRequiredService<AppRuntimeProcessTransport>().Transport, provider.GetRequiredService<NotificationCenterService>(),
            provider.GetRequiredService<BackgroundTaskQueueService>(), provider.GetRequiredService<AppPackageResourceAssemblyRegistry>(),
            provider.GetRequiredService<IUiDispatcher>())
        {
            PackagePolicy = provider.GetRequiredService<AppProcessPolicySupervisor>(),
            DeveloperServices = provider.GetRequiredService<AppPackageDeveloperServices>(),
        });
        services.AddSingleton<WindowLauncherFactory>();
        services.AddSingleton<MainWindowFactory>();
        services.AddSingleton<SettingsWindowFactory>();
        services.AddSingleton<PackagesWindowFactory>();
        services.AddSingleton<StacksWindowFactory>();
        services.AddSingleton<StackWizardWindowFactory>();
        services.AddSingleton<ShellStartupCoordinator>();

        return services;
    }
}
