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
    private static readonly TimeSpan FailedActivationRetirementTimeout = TimeSpan.FromSeconds(5);
    private static readonly Type[] ReservedServiceTypes =
    [
        typeof(IPackageContext),
        typeof(ILoggerFactory),
        typeof(ILogger<>),
        typeof(IPackageExtensionCatalog),
        typeof(IPackageExtensionInvocationCatalog),
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
        PackageExtensionOwnerActivation? extensionOwner = null;
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
            extensionOwner = extensionCatalog.BeginOwnerActivation(package.PackageId);
            var packageContext = new RuntimePackageContext(package.PackageId, package.Version, package.ShadowFolder, paths.PackageDataRootPath);
            var packageServices = new ConstrainedPackageServiceCollection(ReservedServiceTypes);
            module?.ConfigureRuntimeServices(packageServices, packageContext);
            var services = new ServiceCollection();
            packageServices.CopyTo(services);
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.Logging.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
            services.AddSingleton<IPackageExtensionInvocationCatalog>(extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
            services.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
            services.AddSingleton<IPackageCallbackClient>(NullPackageCallbackClient.Instance);
            serviceProvider = services.BuildServiceProvider();

            var contributions = new RuntimePackageContributionRegistry(
                serviceProvider,
                extensionCatalog,
                package.PackageId,
                extensionOwner);
            using (var extensionBatch = extensionCatalog.BeginBatch(
                       extensionOwner,
                       PackageExtensionCatalogChangeReason.PackageActivated))
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
                ExtensionOwner = extensionOwner,
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
            var retirement = extensionOwner is null
                ? PackageExtensionOwnerRetirement.Completed(package.PackageId)
                : extensionCatalog.BeginOwnerRetirement(extensionOwner, PackageExtensionCatalogChangeReason.PackageFaulted);
            var cleanup = CleanupFailedActivationAsync(
                package.PackageId,
                retirement,
                serviceProvider,
                loadContext);
            try
            {
                await cleanup.WaitAsync(FailedActivationRetirementTimeout);
            }
            catch (TimeoutException cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Package {PackageId} activation cleanup exceeded the owner retirement deadline; its provider and load context remain quarantined",
                    package.PackageId);
                _ = ObserveFailedActivationCleanupAsync(package.PackageId, cleanup);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Failed to clean up package {PackageId} after activation failed",
                    package.PackageId);
            }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogError(exception, "Failed to activate package {PackageId}", package.PackageId);
            errors.Add($"Failed to activate package '{package.PackageId}': {exception.Message}");
            return Failed(package, exception.Message);
        }
    }

    private async Task CleanupFailedActivationAsync(
        string packageId,
        PackageExtensionOwnerRetirement retirement,
        ServiceProvider? serviceProvider,
        RuntimePackageLoadContext? loadContext)
    {
        await retirement.Completion.ConfigureAwait(false);
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
                    packageId);
            }
        }
        loadContext?.Unload();
    }

    private async Task ObserveFailedActivationCleanupAsync(string packageId, Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deferred activation cleanup failed for package {PackageId}", packageId);
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
