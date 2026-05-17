using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed class AppPackageServiceProviderFactory(
    AppPackageExtensionCatalog extensionCatalog,
    IPackageShellViewService? shellViewService,
    IPackageSettingsNavigationService? settingsNavigationService,
    NotificationCenterService? notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue)
{
    public ServiceProvider Create(
        ActivePackageDescriptor package,
        AppPackageContext packageContext,
        ISunderPackageModule module)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageContext);
        services.AddSingleton<ILoggerFactory>(packageContext.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
        services.AddSingleton<IPackageShellViewService>(shellViewService ?? DisabledPackageShellViewService.Instance);
        services.AddSingleton<IPackageSettingsNavigationService>(settingsNavigationService ?? NullPackageSettingsNavigationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue>(_ =>
        {
            var packageBackgroundProcessQueue = new PackageScopedBackgroundProcessQueue(package.PackageId, package.DisplayName, backgroundProcessQueue);
            packageBackgroundProcessQueue.Start();
            return packageBackgroundProcessQueue;
        });
        services.AddSingleton<IPackageNotificationService>(notificationCenter is null
            ? NullPackageNotificationService.Instance
            : new AppPackageNotificationService(notificationCenter, package.PackageId, package.DisplayName));
        module.ConfigureServices(services, packageContext);
        return services.BuildServiceProvider();
    }
}
