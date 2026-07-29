using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;

namespace Sunder.App.Services;

internal sealed class AppPackageServiceProviderFactory(
    AppPackageExtensionCatalog extensionCatalog,
    IPackageShellViewService? shellViewService,
    IPackageSettingsNavigationService? settingsNavigationService,
    NotificationCenterService? notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue,
    AppPackageGenerationPublication publication,
    Guid? ownerId = null)
{
    private static readonly Type[] ReservedServiceTypes =
    [
        typeof(IPackageContext),
        typeof(IPackageRuntimeClient),
        typeof(IPackageCallbackClient),
        typeof(ILoggerFactory),
        typeof(ILogger<>),
        typeof(IPackageExtensionCatalog),
        typeof(IPackageExtensionInvocationCatalog),
        typeof(IPackageShellViewService),
        typeof(IPackageSettingsNavigationService),
        typeof(IBackgroundProcessQueue),
        typeof(IPackageNotificationService),
    ];

    internal AppPackageGenerationPublication Publication => publication;

    public ServiceProvider Create(
        ActivePackageDescriptor package,
        AppPackageContext packageContext,
        ISunderAppPackageModule? module)
    {
        var packageServices = new ConstrainedPackageServiceCollection(ReservedServiceTypes);
        module?.ConfigureAppServices(packageServices, packageContext);

        var services = new ServiceCollection();
        packageServices.CopyTo(services);
        services.AddSingleton<IPackageContext>(_ => packageContext);
        services.AddSingleton<IPackageRuntimeClient>(packageContext.Runtime);
        services.AddSingleton<IPackageCallbackClient>(packageContext.Callbacks);
        services.AddSingleton<ILoggerFactory>(packageContext.Logging.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
        services.AddSingleton<IPackageExtensionInvocationCatalog>(
            extensionCatalog.CreateInvocationCatalog(package.PackageId));
        services.AddSingleton<IPackageShellViewService>(shellViewService ?? DisabledPackageShellViewService.Instance);
        services.AddSingleton<IPackageSettingsNavigationService>(settingsNavigationService ?? NullPackageSettingsNavigationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue>(_ =>
        {
            var packageBackgroundProcessQueue = new AppPackageBackgroundProcessQueue(
                new PackageScopedBackgroundProcessQueue(
                    package.PackageId,
                    package.DisplayName,
                    backgroundProcessQueue,
                    ownerId),
                publication);
            packageBackgroundProcessQueue.Start();
            return packageBackgroundProcessQueue;
        });
        services.AddSingleton<IPackageNotificationService>(notificationCenter is null
            ? NullPackageNotificationService.Instance
            : new AppPackageNotificationService(notificationCenter, package.PackageId, package.DisplayName, publication));
        return services.BuildServiceProvider();
    }
}
