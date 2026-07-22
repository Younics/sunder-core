using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageActivator(ILogger logger, RuntimePackagePaths paths)
{
    private static readonly Type[] ReservedServiceTypes =
    [
        typeof(IPackageContext),
        typeof(ILoggerFactory),
        typeof(ILogger<>),
        typeof(IPackageExtensionCatalog),
        typeof(IPackageShellViewService),
        typeof(IPackageSettingsNavigationService),
        typeof(IPackageNotificationService),
        typeof(IPackageRuntimeClient),
        typeof(IPackageCallbackClient),
    ];

    public async Task<PackageActivationResult> ActivateAsync(
        PreparedRuntimePackage package,
        RuntimeSharedAssemblyRegistry sharedAssemblies,
        RuntimePackageExtensionCatalog extensionCatalog,
        ICollection<string> warnings,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        RuntimePackageLoadContext? loadContext = null;
        ServiceProvider? serviceProvider = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            loadContext = new RuntimePackageLoadContext(package.PackageId, package.EntryAssemblyPath, sharedAssemblies);
            var entryAssembly = loadContext.LoadPackageEntryAssembly();
            var moduleResolution = PackageModuleShapeReader.Read(entryAssembly.Location)
                .Resolve(PackageHostRoleMetadataValue.Runtime);
            if (moduleResolution.Error is not null)
            {
                errors.Add($"Package '{package.PackageId}' {moduleResolution.Error}");
                loadContext.Unload();
                return Failed(package, moduleResolution.Error);
            }
            var moduleType = moduleResolution.TypeName is null
                ? null
                : entryAssembly.GetType(moduleResolution.TypeName, throwOnError: true);

            var moduleInstance = moduleType is null ? null : Activator.CreateInstance(moduleType);
            if (moduleType is not null && moduleInstance is not ISunderRuntimePackageModule)
            {
                var message = $"Module '{moduleType.FullName}' does not implement ISunderRuntimePackageModule.";
                errors.Add($"Package '{package.PackageId}' {message}");
                loadContext.Unload();
                return Failed(package, message);
            }
            var module = moduleInstance as ISunderRuntimePackageModule;
            var packageContext = new RuntimePackageContext(package.PackageId, package.Version, package.ShadowFolder, paths.PackageDataRootPath);
            var packageServices = new ConstrainedPackageServiceCollection(ReservedServiceTypes);
            module?.ConfigureRuntimeServices(packageServices, packageContext);
            var services = new ServiceCollection();
            packageServices.CopyTo(services);
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.Logging.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
            services.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
            services.AddSingleton<IPackageCallbackClient>(NullPackageCallbackClient.Instance);
            serviceProvider = services.BuildServiceProvider();

            var contributions = new RuntimePackageContributionRegistry(serviceProvider, extensionCatalog, package.PackageId);
            using (var extensionBatch = extensionCatalog.BeginBatch(PackageExtensionCatalogChangeReason.PackageActivated))
            {
                module?.RegisterRuntimeContributions(contributions, serviceProvider);
                extensionBatch.Commit();
            }
            packageContext.PackageSettings.Schema = contributions.SettingsSchema;
            var loadedPackage = new ActiveLoadedPackage(
                BuildDescriptor(package.Activation, true, PackageReadinessState.Ready, []),
                package.Source,
                ToProtocolSettingsSchema(package.PackageId, package.Activation.Name, contributions.SettingsSchema),
                packageContext.Storage.State,
                packageContext.SecretsStore,
                serviceProvider.GetService<IPackageAuthHandler>(),
                CollectCallbackHandlers(serviceProvider),
                contributions.BackgroundServices,
                serviceProvider,
                loadContext,
                packageContext.Settings)
            {
                CanonicalSettingsSchema = contributions.SettingsSchema,
                RuntimeOperations = contributions.RuntimeOperations,
                RuntimeStreams = contributions.RuntimeStreams,
            };
            var descriptor = BuildSessionDescriptor(package.Activation, true, PackageReadinessState.Ready, packageViews: []);
            if (!contributions.HasRegisteredExtensions
                && !contributions.HasRegisteredBackgroundServices
                && contributions.SettingsSchema is null
                && contributions.RuntimeOperations.Count == 0
                && contributions.RuntimeStreams.Count == 0)
            {
                warnings.Add($"Package '{package.PackageId}' loaded without any Runtime contributions.");
            }
            return new PackageActivationResult(true, loadedPackage, descriptor);
        }
        catch (Exception exception)
        {
            if (serviceProvider is not null)
            {
                try
                {
                    await PackageSessionLifecycle.DisposeOwnedServiceProviderAsync(serviceProvider);
                }
                catch (Exception cleanupException)
                {
                    logger.LogWarning(
                        cleanupException,
                        "Failed to dispose services after package {PackageId} activation failed",
                        package.PackageId);
                }
            }
            loadContext?.Unload();
            extensionCatalog.RemovePackage(package.PackageId, PackageExtensionCatalogChangeReason.PackageFaulted);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogError(exception, "Failed to activate package {PackageId}", package.PackageId);
            errors.Add($"Failed to activate package '{package.PackageId}': {exception.Message}");
            return Failed(package, exception.Message);
        }
    }

    private static PackageActivationResult Failed(PreparedRuntimePackage package, string message)
        => new(false, null, BuildSessionDescriptor(
            package.Activation,
            false,
            PackageReadinessState.Failed,
            failureOrigin: PackageFailureOrigin.RuntimeActivation,
            lastError: message,
            failureCount: 1));

    private static IReadOnlyDictionary<string, IPackageCallbackHandler> CollectCallbackHandlers(IServiceProvider serviceProvider)
    {
        var handlers = serviceProvider.GetServices<IPackageCallbackHandler>()
            .Where(handler => !string.IsNullOrWhiteSpace(handler.CallbackHandlerId))
            .ToDictionary(handler => handler.CallbackHandlerId, StringComparer.OrdinalIgnoreCase);
        if (serviceProvider.GetService<IPackageAuthHandler>() is IPackageCallbackHandler authHandler)
        {
            handlers[authHandler.CallbackHandlerId] = authHandler;
        }
        return handlers;
    }

}
