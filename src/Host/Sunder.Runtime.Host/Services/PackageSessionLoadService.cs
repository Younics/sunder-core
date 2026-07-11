using System.Text.Json;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionLoadService(ILogger logger, RuntimePackagePaths? paths = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<PackageSessionLoadResult> LoadInstalledAsync(IReadOnlyList<InstalledPackageRecord> packages, bool startBackgroundServices = true)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.CleanupStaleSessions();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();

        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new PackageSessionFileMaterializer();
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var enabledPackages = packages.Where(static package => package.IsEnabled).ToArray();

        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            var manifest = ToManifest(package);
            if (!package.IsEnabled)
            {
                sessionPackages[package.PackageId] = BuildSessionDescriptor(
                    manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Disabled);
                continue;
            }

            var errorCount = errors.Count;
            var preparedPackage = PrepareInstalledPackage(index, package, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is not null)
            {
                preparedCandidates.Add(preparedPackage);
                continue;
            }

            sessionPackages[package.PackageId] = BuildSessionDescriptor(
                manifest,
                isEnabled: false,
                readiness: PackageReadinessState.Failed,
                failureOrigin: PackageFailureOrigin.RuntimeActivation,
                lastError: errors.Skip(errorCount).LastOrDefault() ?? "Installed package could not be prepared for loading.",
                failureCount: 1);
        }

        if (enabledPackages.Length == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings, errors);
        }

        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, startBackgroundServices);
    }

    public async Task<PackageSessionLoadResult> LoadInstalledWithDevOverlaysAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        IReadOnlyList<string> devFolders,
        bool startBackgroundServices = true)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.CleanupStaleSessions();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();

        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new PackageSessionFileMaterializer();
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var devPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var devFolder in devFolders)
        {
            var preparedPackage = PreparePackage(index++, devFolder, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is null)
            {
                continue;
            }

            preparedCandidates.Add(preparedPackage);
            devPackageIds.Add(preparedPackage.PackageId);
        }

        foreach (var package in packages)
        {
            if (devPackageIds.Contains(package.PackageId))
            {
                continue;
            }

            var manifest = ToManifest(package);
            if (!package.IsEnabled)
            {
                sessionPackages[package.PackageId] = BuildSessionDescriptor(
                    manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Disabled);
                continue;
            }

            var errorCount = errors.Count;
            var preparedPackage = PrepareInstalledPackage(index++, package, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is not null)
            {
                preparedCandidates.Add(preparedPackage);
                continue;
            }

            sessionPackages[package.PackageId] = BuildSessionDescriptor(
                manifest,
                isEnabled: false,
                readiness: PackageReadinessState.Failed,
                failureOrigin: PackageFailureOrigin.RuntimeActivation,
                lastError: errors.Skip(errorCount).LastOrDefault() ?? "Installed package could not be prepared for loading.",
                failureCount: 1);
        }

        if (preparedCandidates.Count == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings, errors);
        }

        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, startBackgroundServices);
    }

    private async Task<PackageSessionLoadResult> LoadPreparedPackagesAsync(
        string sessionFolder,
        IReadOnlyList<PreparedRuntimePackage> preparedCandidates,
        ICollection<string> warnings,
        ICollection<string> errors,
        IDictionary<string, SessionPackageDescriptor> initialSessionPackages,
        bool startBackgroundServices)
    {
        if (preparedCandidates.Count == 0 && initialSessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings.ToArray(), errors.ToArray());
        }

        var orderedPackages = new PackageLoadPlanner().ResolveLoadOrder(preparedCandidates, errors);
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry;
        try
        {
            sharedAssemblyRegistry = new RuntimeSharedAssemblyRegistry(orderedPackages.Select(x => x.LibraryFolder));
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(null, warnings.ToArray(), errors.ToArray());
        }

        var loadedPackages = new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase);
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(initialSessionPackages, StringComparer.OrdinalIgnoreCase);
        var loadedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionCatalog = new RuntimePackageExtensionCatalog();

        foreach (var preparedPackage in orderedPackages)
        {
            if (!preparedPackage.Dependencies.All(loadedPackageIds.Contains))
            {
                var message = $"Skipped '{preparedPackage.PackageId}' because one of its dependencies did not load successfully.";
                errors.Add(message);
                sessionPackages[preparedPackage.PackageId] = BuildSessionDescriptor(
                    preparedPackage.Manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: message,
                    failureCount: 1);
                continue;
            }

            var activation = await TryActivatePackageAsync(preparedPackage, sharedAssemblyRegistry, extensionCatalog, warnings, errors, startBackgroundServices);
            if (!activation.Success)
            {
                sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
                continue;
            }

            loadedPackageIds.Add(preparedPackage.PackageId);
            loadedPackages[preparedPackage.PackageId] = activation.LoadedPackage!;
            sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
        }

        var session = new ActivePackageSession(
            sessionFolder,
            loadedPackages,
            sessionPackages,
            extensionCatalog,
            sharedAssemblyRegistry,
            backgroundServicesStarted: startBackgroundServices);
        return new PackageSessionLoadResult(session, warnings.ToArray(), errors.ToArray());
    }

    private static PreparedRuntimePackage? PreparePackage(
        int index,
        string folder,
        string sessionFolder,
        PackageSessionFileMaterializer fileMaterializer,
        ICollection<string> errors)
    {
        if (!Directory.Exists(folder))
        {
            errors.Add($"Dev package folder '{folder}' does not exist.");
            return null;
        }

        var manifestPath = Path.Combine(folder, "sunder-package.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add($"Dev package folder '{folder}' does not contain sunder-package.json.");
            return null;
        }

        var shadowFolderName = $"{index:D2}-{SanitizeFolderName(Path.GetFileName(folder))}";
        var shadowFolder = Path.Combine(sessionFolder, shadowFolderName);
        fileMaterializer.MaterializeDirectory(folder, shadowFolder);

        return PrepareMaterializedPackage(
            folder,
            new RuntimePackageSource(string.Empty, PackageSourceKind.Dev, folder, shadowFolder),
            shadowFolder,
            errors);
    }

    private static PreparedRuntimePackage? PrepareInstalledPackage(
        int index,
        InstalledPackageRecord package,
        string sessionFolder,
        PackageSessionFileMaterializer fileMaterializer,
        ICollection<string> errors)
    {
        if (!Directory.Exists(package.InstallPath))
        {
            errors.Add($"Installed package folder '{package.InstallPath}' does not exist.");
            return null;
        }

        if (!File.Exists(package.ManifestPath))
        {
            errors.Add($"Installed package '{package.PackageId}' does not contain manifest/sunder-package.json.");
            return null;
        }

        var shadowFolderName = $"{index:D2}-{SanitizeFolderName(package.PackageId)}";
        var shadowFolder = Path.Combine(sessionFolder, shadowFolderName);
        Directory.CreateDirectory(shadowFolder);
        File.Copy(package.ManifestPath, Path.Combine(shadowFolder, "sunder-package.json"), overwrite: true);

        if (Directory.Exists(package.LibraryFolder))
        {
            fileMaterializer.MaterializeDirectory(package.LibraryFolder, Path.Combine(shadowFolder, "lib"));
        }

        var assetFolder = Path.Combine(package.InstallPath, "payload", "assets");
        if (Directory.Exists(assetFolder))
        {
            fileMaterializer.MaterializeDirectory(assetFolder, Path.Combine(shadowFolder, "assets"));
        }

        return PrepareMaterializedPackage(
            package.InstallPath,
            new RuntimePackageSource(package.PackageId, PackageSourceKind.Installed, package.InstallPath, shadowFolder),
            shadowFolder,
            errors);
    }

    private static PreparedRuntimePackage? PrepareMaterializedPackage(
        string sourceFolder,
        RuntimePackageSource source,
        string shadowFolder,
        ICollection<string> errors)
    {

        var shadowManifestPath = Path.Combine(shadowFolder, "sunder-package.json");
        RuntimePackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RuntimePackageManifest>(File.ReadAllText(shadowManifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse '{shadowManifestPath}': {ex.Message}");
            return null;
        }

        var validationErrors = RuntimePackageManifestValidator.Validate(manifest, shadowFolder).ToArray();
        if (validationErrors.Length > 0)
        {
            foreach (var validationError in validationErrors)
            {
                errors.Add(validationError);
            }

            return null;
        }

        var libraryFolder = Path.Combine(shadowFolder, "lib");
        var entryAssemblyPath = Path.Combine(libraryFolder, manifest!.EntryAssembly!);
        var dependencies = (manifest.DependsOn ?? [])
            .Select(x => x.PackageId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PreparedRuntimePackage(
            sourceFolder,
            source with { PackageId = manifest.Id! },
            shadowFolder,
            libraryFolder,
            manifest.Id!,
            manifest.Version!,
            manifest,
            entryAssemblyPath,
            dependencies
        );
    }

    private static RuntimePackageManifest ToManifest(InstalledPackageRecord package)
        => new()
        {
            ManifestVersion = 1,
            Id = package.PackageId,
            Name = package.Name,
            Summary = package.Summary,
            Version = package.Version,
            EntryAssembly = package.EntryAssembly,
            Icon = package.Icon,
            DependsOn = package.DependsOn
                .Select(dependency => new RuntimePackageDependencyManifest
                {
                    PackageId = dependency.PackageId,
                    VersionRange = dependency.VersionRange,
                })
                .ToArray(),
        };

    private static IReadOnlyDictionary<string, IPackageCallbackHandler> CollectCallbackHandlers(IServiceProvider serviceProvider)
    {
        var handlers = new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in serviceProvider.GetServices<IPackageCallbackHandler>())
        {
            if (!string.IsNullOrWhiteSpace(handler.CallbackHandlerId))
            {
                handlers[handler.CallbackHandlerId] = handler;
            }
        }

        if (serviceProvider.GetService<IPackageAuthHandler>() is { } authHandler)
        {
            var callbackHandler = (IPackageCallbackHandler)authHandler;
            handlers[callbackHandler.CallbackHandlerId] = callbackHandler;
        }

        return handlers;
    }

    private async Task<PackageActivationResult> TryActivatePackageAsync(
        PreparedRuntimePackage preparedPackage,
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry,
        RuntimePackageExtensionCatalog extensionCatalog,
        ICollection<string> warnings,
        ICollection<string> errors,
        bool startBackgroundServices
    )
    {
        RuntimePackageLoadContext? loadContext = null;
        ServiceProvider? serviceProvider = null;
        var startedBackgroundServices = new List<IPackageBackgroundService>();

        try
        {
            loadContext = new RuntimePackageLoadContext(preparedPackage.PackageId, preparedPackage.EntryAssemblyPath, sharedAssemblyRegistry);
            var entryAssembly = loadContext.LoadPackageEntryAssembly();
            var moduleType = ResolvePackageModuleType(entryAssembly, out var moduleResolutionError);
            if (moduleType is null && moduleResolutionError is not null)
            {
                errors.Add($"Package '{preparedPackage.PackageId}' {moduleResolutionError}");
                loadContext.Unload();
                return new PackageActivationResult(false, null, BuildSessionDescriptor(
                    preparedPackage.Manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: moduleResolutionError,
                    failureCount: 1));
            }

            ISunderRuntimePackageModule? module = null;
            var moduleInstance = moduleType is null ? null : Activator.CreateInstance(moduleType);
            if (moduleType is not null && moduleInstance is not ISunderRuntimePackageModule)
            {
                errors.Add($"Package '{preparedPackage.PackageId}' module '{moduleType.FullName}' does not implement ISunderRuntimePackageModule.");
                loadContext.Unload();
                return new PackageActivationResult(false, null, BuildSessionDescriptor(
                    preparedPackage.Manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: $"Module '{moduleType.FullName}' does not implement ISunderRuntimePackageModule.",
                    failureCount: 1));
            }
            else if (moduleType is not null)
            {
                module = (ISunderRuntimePackageModule)moduleInstance!;
            }

            var packageContext = new RuntimePackageContext(
                preparedPackage.PackageId,
                preparedPackage.Version,
                preparedPackage.ShadowFolder,
                (paths ?? new RuntimePackagePaths()).PackageDataRootPath);
            var services = new ServiceCollection();
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
            services.AddSingleton<IPackageSessionService>(NullPackageSessionService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);

            module?.ConfigureRuntimeServices(services, packageContext);
            serviceProvider = services.BuildServiceProvider();

            var contributionRegistry = new RuntimePackageContributionRegistry(serviceProvider, extensionCatalog, preparedPackage.PackageId);
            module?.RegisterRuntimeContributions(contributionRegistry, serviceProvider);

            foreach (var backgroundService in contributionRegistry.BackgroundServices)
            {
                if (startBackgroundServices)
                {
                    await backgroundService.StartAsync(CancellationToken.None);
                    startedBackgroundServices.Add(backgroundService);
                }
            }

            var loadedPackage = new ActiveLoadedPackage(
                BuildDescriptor(preparedPackage.Manifest, isEnabled: true, readiness: PackageReadinessState.Ready, packageViews: []),
                preparedPackage.Source,
                ToProtocolConfigurationSchema(contributionRegistry.ConfigurationSchema),
                packageContext.Storage.State,
                packageContext.SecretsStore,
                serviceProvider.GetService<IPackageAuthHandler>(),
                CollectCallbackHandlers(serviceProvider),
                contributionRegistry.BackgroundServices,
                serviceProvider,
                loadContext
            );
            var sessionPackage = BuildSessionDescriptor(
                preparedPackage.Manifest,
                isEnabled: true,
                readiness: PackageReadinessState.Ready,
                packageViews: []);

            if (!contributionRegistry.HasRegisteredExtensions
                && !contributionRegistry.HasRegisteredBackgroundServices
                && contributionRegistry.ConfigurationSchema is null)
            {
                warnings.Add($"Package '{preparedPackage.PackageId}' loaded without any Runtime contributions.");
            }

            return new PackageActivationResult(true, loadedPackage, sessionPackage);
        }
        catch (Exception ex)
        {
            await PackageSessionLifecycle.StopBackgroundServicesAsync(startedBackgroundServices, preparedPackage.PackageId, logger);
            if (serviceProvider is not null)
            {
                await PackageSessionLifecycle.DisposeOwnedServiceProviderAsync(serviceProvider);
            }
            loadContext?.Unload();
            extensionCatalog.RemovePackage(preparedPackage.PackageId, PackageExtensionCatalogChangeReason.PackageFaulted);
            logger.LogError(ex, "Failed to activate dev package {PackageId}", preparedPackage.PackageId);
            errors.Add($"Failed to activate dev package '{preparedPackage.PackageId}': {ex.Message}");
            var sessionPackage = BuildSessionDescriptor(
                preparedPackage.Manifest,
                isEnabled: false,
                readiness: PackageReadinessState.Failed,
                failureOrigin: PackageFailureOrigin.RuntimeActivation,
                lastError: ex.Message,
                failureCount: 1);
            return new PackageActivationResult(false, null, sessionPackage);
        }
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
                + string.Join(", ", moduleTypes.Select(static type => type.FullName));
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
                if (implementation.Interface.Kind != HandleKind.TypeReference)
                {
                    return false;
                }

                var interfaceType = metadata.GetTypeReference((TypeReferenceHandle)implementation.Interface);
                return metadata.GetString(interfaceType.Namespace) == "Sunder.Sdk.Abstractions"
                    && metadata.GetString(interfaceType.Name) == nameof(ISunderRuntimePackageModule);
            });
            if (!implementsRuntimeRole)
            {
                continue;
            }

            var typeNamespace = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            yield return string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for failed reload attempts.
        }
    }

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "package";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray());
    }
}
