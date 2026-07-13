using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageActivator(ILogger logger, RuntimePackagePaths paths)
{
    public async Task<PackageActivationResult> ActivateAsync(
        PreparedRuntimePackage package,
        RuntimeSharedAssemblyRegistry sharedAssemblies,
        RuntimePackageExtensionCatalog extensionCatalog,
        ICollection<string> warnings,
        ICollection<string> errors,
        bool startBackgroundServices,
        CancellationToken cancellationToken)
    {
        RuntimePackageLoadContext? loadContext = null;
        ServiceProvider? serviceProvider = null;
        var startedBackgroundServices = new List<IPackageBackgroundService>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            loadContext = new RuntimePackageLoadContext(package.PackageId, package.EntryAssemblyPath, sharedAssemblies);
            var entryAssembly = loadContext.LoadPackageEntryAssembly();
            var moduleType = ResolvePackageModuleType(entryAssembly, out var moduleResolutionError);
            if (moduleType is null && moduleResolutionError is not null)
            {
                errors.Add($"Package '{package.PackageId}' {moduleResolutionError}");
                loadContext.Unload();
                return Failed(package, moduleResolutionError);
            }

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
            var services = new ServiceCollection();
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
            services.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
            services.AddSingleton<IPackageCallbackClient>(NullPackageCallbackClient.Instance);
            module?.ConfigureRuntimeServices(services, packageContext);
            serviceProvider = services.BuildServiceProvider();

            var contributions = new RuntimePackageContributionRegistry(serviceProvider, extensionCatalog, package.PackageId);
            module?.RegisterRuntimeContributions(contributions, serviceProvider);
            packageContext.PackageSettings.Schema = contributions.ConfigurationSchema;
            foreach (var backgroundService in contributions.BackgroundServices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!startBackgroundServices) continue;
                await backgroundService.StartAsync(cancellationToken);
                startedBackgroundServices.Add(backgroundService);
            }

            var loadedPackage = new ActiveLoadedPackage(
                BuildDescriptor(package.Activation, true, PackageReadinessState.Ready, []),
                package.Source,
                ToProtocolConfigurationSchema(contributions.ConfigurationSchema),
                packageContext.Storage.State,
                packageContext.SecretsStore,
                serviceProvider.GetService<IPackageAuthHandler>(),
                CollectCallbackHandlers(serviceProvider),
                contributions.BackgroundServices,
                serviceProvider,
                loadContext,
                packageContext.Settings)
            {
                RuntimeOperations = contributions.RuntimeOperations,
                RuntimeStreams = contributions.RuntimeStreams,
            };
            var descriptor = BuildSessionDescriptor(package.Activation, true, PackageReadinessState.Ready, packageViews: []);
            if (!contributions.HasRegisteredExtensions
                && !contributions.HasRegisteredBackgroundServices
                && contributions.ConfigurationSchema is null
                && contributions.RuntimeOperations.Count == 0
                && contributions.RuntimeStreams.Count == 0)
            {
                warnings.Add($"Package '{package.PackageId}' loaded without any Runtime contributions.");
            }
            return new PackageActivationResult(true, loadedPackage, descriptor);
        }
        catch (Exception exception)
        {
            await PackageSessionLifecycle.StopBackgroundServicesAsync(startedBackgroundServices, package.PackageId, logger);
            if (serviceProvider is not null) await PackageSessionLifecycle.DisposeOwnedServiceProviderAsync(serviceProvider);
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

    private static Type? ResolvePackageModuleType(Assembly entryAssembly, out string? error)
    {
        var moduleTypes = FindRuntimeModuleTypeNames(entryAssembly.Location)
            .Select(typeName => entryAssembly.GetType(typeName, throwOnError: true)!)
            .ToArray();
        if (moduleTypes.Length == 0)
        {
            error = null;
            return null;
        }
        if (moduleTypes.Length > 1)
        {
            error = "contains multiple public ISunderRuntimePackageModule implementations: "
                + string.Join(", ", moduleTypes.Select(type => type.FullName));
            return null;
        }
        var moduleType = moduleTypes[0];
        if (moduleType.GetConstructor(Type.EmptyTypes) is null)
        {
            error = $"module '{moduleType.FullName}' must declare a public parameterless constructor.";
            return null;
        }
        error = null;
        return moduleType;
    }

    private static IEnumerable<string> FindRuntimeModuleTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public
                || (type.Attributes & TypeAttributes.Abstract) != 0
                || (type.Attributes & TypeAttributes.Interface) != 0)
            {
                continue;
            }
            var implementsRuntimeRole = type.GetInterfaceImplementations().Any(interfaceHandle =>
            {
                var implementation = metadata.GetInterfaceImplementation(interfaceHandle);
                if (implementation.Interface.Kind != HandleKind.TypeReference) return false;
                var interfaceType = metadata.GetTypeReference((TypeReferenceHandle)implementation.Interface);
                return metadata.GetString(interfaceType.Namespace) == "Sunder.Sdk.Abstractions"
                       && metadata.GetString(interfaceType.Name) == nameof(ISunderRuntimePackageModule);
            });
            if (!implementsRuntimeRole) continue;
            var typeNamespace = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            yield return string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
        }
    }
}
