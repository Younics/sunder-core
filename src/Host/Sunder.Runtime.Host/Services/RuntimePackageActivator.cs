using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Rpc;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageActivator(
    ILogger logger,
    RuntimePackagePaths paths,
    RuntimeRpcBroker? rpcBroker = null,
    RuntimeProcessPolicyOptions? processPolicy = null,
    CancellationToken hostStopping = default)
{
    private static readonly TimeSpan FailedActivationRetirementTimeout = TimeSpan.FromSeconds(5);
    private static readonly Type[] ReservedServiceTypes =
    [
        typeof(IPackageContext),
        typeof(ILoggerFactory),
        typeof(ILogger<>),
        typeof(IPackageShellViewService),
        typeof(IPackageSettingsNavigationService),
        typeof(IPackageNotificationService),
        typeof(IPackageRuntimeClient),
        typeof(IPackageCallbackClient),
        typeof(ISunderRpcClient),
    ];

    public async Task<PackageActivationResult> ActivateAsync(
        PreparedRuntimePackage package,
        RuntimeSharedAssemblyRegistry sharedAssemblies,
        ICollection<string> warnings,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        RuntimePackageLoadContext? loadContext = null;
        ServiceProvider? serviceProvider = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.SelectedTargetKey is not { } targetKey
                || package.SelectedTarget is not { } target
                || package.EntryAssemblyPath is null)
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' does not have a selected exact Runtime target.");
            }
            if (string.Equals(target.Kind, SunderPackageFormat.ProcessTargetKind, StringComparison.Ordinal))
            {
                if (rpcBroker is null)
                {
                    throw new InvalidOperationException(
                        $"Package '{package.PackageId}' process Runtime target requires the Runtime RPC broker.");
                }
                var processActivationId = Guid.NewGuid();
                var processPackageContext = new RuntimePackageContext(
                    package.PackageId,
                    package.Version,
                    package.ShadowFolder,
                    paths.PackageDataRootPath);
                var worker = new ProcessRuntimeWorker(
                    logger,
                    package,
                    processPackageContext,
                    processActivationId,
                    rpcBroker,
                    processPolicy,
                    hostStopping);
                var processServices = new ServiceCollection();
                processServices.AddSingleton(_ => worker);
                serviceProvider = processServices.BuildServiceProvider();
                _ = serviceProvider.GetRequiredService<ProcessRuntimeWorker>();
                var providers = CreateProcessProviderRegistrations(package, worker);
                var processLoadedPackage = new ActiveLoadedPackage(
                    BuildDescriptor(package.Activation, true, PackageReadinessState.Ready, []),
                    package.Source,
                    SettingsSchema: null,
                    processPackageContext.Storage.State,
                    processPackageContext.SecretsStore,
                    AuthHandler: null,
                    CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                    BackgroundServices: [worker],
                    serviceProvider,
                    LoadContext: null,
                    processPackageContext.Settings)
                {
                    RuntimeActivationId = processActivationId,
                    RpcProviders = providers,
                    RpcContracts = package.RpcContracts
                                   ?? new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal),
                    RpcContractUses = (package.Source.Manifest?.UsesContracts ?? [])
                        .Where(static use => use is not null)
                        .Select(static use => use!)
                        .ToArray(),
                    RpcManifestSha256 = package.ManifestSha256 ?? string.Empty,
                };
                if (providers.Count == 0)
                {
                    warnings.Add($"Package '{package.PackageId}' process Runtime target loaded without any declared RPC providers.");
                }
                return new PackageActivationResult(
                    true,
                    processLoadedPackage,
                    BuildSessionDescriptor(package.Activation, true, PackageReadinessState.Ready, packageViews: []));
            }
            if (!string.Equals(target.Kind, SunderPackageFormat.DotnetTargetKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' Runtime target '{targetKey}' kind '{target.Kind}' is unsupported; expected '{SunderPackageFormat.DotnetTargetKind}'.");
            }
            loadContext = new RuntimePackageLoadContext(
                package.PackageId,
                package.EntryAssemblyPath,
                targetKey.Rid,
                sharedAssemblies);
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
            var runtimeActivationId = Guid.NewGuid();
            var packageContext = new RuntimePackageContext(package.PackageId, package.Version, package.ShadowFolder, paths.PackageDataRootPath);
            var packageServices = new ConstrainedPackageServiceCollection(ReservedServiceTypes);
            module?.ConfigureRuntimeServices(packageServices, packageContext);
            var services = new ServiceCollection();
            packageServices.CopyTo(services);
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.Logging.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
            services.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
            services.AddSingleton<IPackageCallbackClient>(NullPackageCallbackClient.Instance);
            services.AddSingleton<ISunderRpcClient>(rpcBroker is null
                ? UnavailableRuntimeRpcClient.Instance
                : new RuntimeRpcClient(
                    rpcBroker,
                    new RuntimeRpcCallerStamp(package.PackageId, runtimeActivationId)));
            serviceProvider = services.BuildServiceProvider();

            var contributions = new RuntimePackageContributionRegistry(
                serviceProvider,
                package.PackageId,
                package.Source.Manifest,
                package.RpcContracts);
            module?.RegisterRuntimeContributions(contributions, serviceProvider);
            contributions.ValidateRpcProviders();
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
                RuntimeActivationId = runtimeActivationId,
                CanonicalSettingsSchema = contributions.SettingsSchema,
                RuntimeOperations = contributions.RuntimeOperations,
                RuntimeStreams = contributions.RuntimeStreams,
                RpcProviders = contributions.RpcProviders,
                RpcContracts = package.RpcContracts
                               ?? new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal),
                RpcContractUses = (package.Source.Manifest?.UsesContracts ?? [])
                    .Where(static use => use is not null)
                    .Select(static use => use!)
                    .ToArray(),
                RpcManifestSha256 = package.ManifestSha256 ?? string.Empty,
            };
            var descriptor = BuildSessionDescriptor(package.Activation, true, PackageReadinessState.Ready, packageViews: []);
            if (!contributions.HasRegisteredBackgroundServices
                && contributions.SettingsSchema is null
                && contributions.RuntimeOperations.Count == 0
                && contributions.RuntimeStreams.Count == 0
                && contributions.RpcProviders.Count == 0)
            {
                warnings.Add($"Package '{package.PackageId}' loaded without any Runtime contributions.");
            }
            return new PackageActivationResult(true, loadedPackage, descriptor);
        }
        catch (Exception exception)
        {
            var cleanup = CleanupFailedActivationAsync(
                package.PackageId,
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
        ServiceProvider? serviceProvider,
        RuntimePackageLoadContext? loadContext)
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

    private static IReadOnlyDictionary<string, RuntimeRpcProviderRegistration> CreateProcessProviderRegistrations(
        PreparedRuntimePackage package,
        ProcessRuntimeWorker worker)
    {
        var contracts = package.RpcContracts
                        ?? new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        var registrations = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal);
        foreach (var declaration in (package.Source.Manifest?.Provides ?? [])
                     .Where(static provider => provider is not null
                                               && string.Equals(
                                                   provider.Role,
                                                   SunderPackageFormat.RuntimeHostRole,
                                                   StringComparison.Ordinal))
                     .Select(static provider => provider!))
        {
            if (!contracts.TryGetValue(
                    PackageSessionPreparer.ContractKey(declaration.ContractId!, declaration.ContractVersion!),
                    out var contract)
                || !string.Equals(contract.Sha256, declaration.ContractSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Process provider '{declaration.ProviderId}' does not resolve its exact validated local RPC contract.");
            }
            if (!registrations.TryAdd(
                    declaration.ProviderId!,
                    new RuntimeRpcProviderRegistration(
                        declaration.ProviderId!,
                        declaration.ContractId!,
                        declaration.ContractVersion!,
                        declaration.ContractSha256!,
                        contract,
                        new ProcessRpcProviderHandler(worker, declaration.ProviderId!),
                        declaration)))
            {
                throw new InvalidDataException(
                    $"Process provider id '{declaration.ProviderId}' is declared more than once for the Runtime role.");
            }
        }
        return registrations;
    }

}
