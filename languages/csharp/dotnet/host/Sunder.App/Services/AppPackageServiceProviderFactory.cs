using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppPackageServiceProviderFactory(
    IPackageShellViewService? shellViewService,
    IPackageSettingsNavigationService? settingsNavigationService,
    NotificationCenterService? notificationCenter,
    BackgroundTaskQueueService backgroundTaskQueue,
    AppPackageGenerationPublication publication,
    Guid? ownerId = null,
    IAppPackageDevelopmentBackend? developerServices = null)
{
    private static readonly Type[] ReservedServiceTypes =
    [
        typeof(IPackageContext),
        typeof(IPackageCallbackClient),
        typeof(ILoggerFactory),
        typeof(ILogger<>),
        typeof(ISunderRpcClient),
        typeof(IPackageShellViewService),
        typeof(IPackageSettingsNavigationService),
        typeof(IPackageDevelopmentService),
        typeof(IPackageLogNavigationService),
        typeof(IPackageAppStateUpdates),
        typeof(IPackageWorkerExecutionObservations),
        typeof(IBackgroundTaskQueue),
        typeof(IPackageNotificationService),
    ];

    internal AppPackageGenerationPublication Publication => publication;

    public ServiceProvider Create(
        ActivePackageDescriptor package,
        AppPackageContext packageContext,
        ISunderAppPackageModule? module,
        ISunderRpcClient rpcClient)
    {
        var packageServices = new ConstrainedPackageServiceCollection(ReservedServiceTypes);
        module?.ConfigureAppServices(packageServices, packageContext);

        var services = new ServiceCollection();
        packageServices.CopyTo(services);
        services.AddSingleton<IPackageContext>(_ => packageContext);
        services.AddSingleton<IPackageCallbackClient>(packageContext.Callbacks);
        services.AddSingleton<ILoggerFactory>(packageContext.Logging.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(rpcClient);
        services.AddSingleton<IPackageShellViewService>(shellViewService ?? DisabledPackageShellViewService.Instance);
        services.AddSingleton<IPackageSettingsNavigationService>(settingsNavigationService ?? NullPackageSettingsNavigationService.Instance);
        services.AddSingleton(_ => new AppPackageDevelopmentService(developerServices, publication, Guid.NewGuid()));
        services.AddSingleton<IPackageDevelopmentService>(provider => provider.GetRequiredService<AppPackageDevelopmentService>());
        services.AddSingleton<IPackageLogNavigationService>(provider => provider.GetRequiredService<AppPackageDevelopmentService>());
        services.AddSingleton<IPackageAppStateUpdates>(new AppPackageStateUpdates(publication));
        services.AddSingleton<IPackageWorkerExecutionObservations>(new AppPackageWorkerExecutionObservations(rpcClient, publication));
        services.AddSingleton<IBackgroundTaskQueue>(_ =>
        {
            var packageBackgroundTaskQueue = new AppPackageBackgroundTaskQueue(
                new PackageScopedBackgroundTaskQueue(
                    package.PackageId,
                    package.DisplayName,
                    backgroundTaskQueue,
                    ownerId),
                publication);
            packageBackgroundTaskQueue.Start();
            return packageBackgroundTaskQueue;
        });
        services.AddSingleton<IPackageNotificationService>(notificationCenter is null
            ? NullPackageNotificationService.Instance
            : new AppPackageNotificationService(notificationCenter, package.PackageId, package.DisplayName, publication));
        return services.BuildServiceProvider();
    }
}
