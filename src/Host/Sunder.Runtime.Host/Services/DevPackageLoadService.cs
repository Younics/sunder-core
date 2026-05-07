using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.Runtime.Host.Services.DevPackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageLoadService(ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<DevPackageLoadSessionResult> LoadAsync(IReadOnlyList<string> folders)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var sessionFolder = Path.Combine(
            Path.GetTempPath(),
            "Sunder.Runtime.Host",
            "dev-sessions",
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new DevSessionFileMaterializer();

        var preparedCandidates = new List<PreparedDevPackage>();
        for (var index = 0; index < folders.Count; index++)
        {
            var preparedPackage = PreparePackage(index, folders[index], sessionFolder, fileMaterializer, errors);
            if (preparedPackage is not null)
            {
                preparedCandidates.Add(preparedPackage);
            }
        }

        return await LoadPreparedPackagesAsync(
            sessionFolder,
            preparedCandidates,
            warnings,
            errors,
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<DevPackageLoadSessionResult> LoadInstalledAsync(IReadOnlyList<InstalledPackageRecord> packages)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var sessionFolder = Path.Combine(
            Path.GetTempPath(),
            "Sunder.Runtime.Host",
            "installed-sessions",
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new DevSessionFileMaterializer();
        var preparedCandidates = new List<PreparedDevPackage>();
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

            var preparedPackage = PrepareInstalledPackage(index, package, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is not null)
            {
                preparedCandidates.Add(preparedPackage);
            }
        }

        if (enabledPackages.Length == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new DevPackageLoadSessionResult(ActiveDevPackageSession.Empty, warnings, errors);
        }

        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages);
    }

    private async Task<DevPackageLoadSessionResult> LoadPreparedPackagesAsync(
        string sessionFolder,
        IReadOnlyList<PreparedDevPackage> preparedCandidates,
        ICollection<string> warnings,
        ICollection<string> errors,
        IDictionary<string, SessionPackageDescriptor> initialSessionPackages)
    {
        if (preparedCandidates.Count == 0 && initialSessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new DevPackageLoadSessionResult(ActiveDevPackageSession.Empty, warnings.ToArray(), errors.ToArray());
        }

        var orderedPackages = new DevPackageLoadPlanner().ResolveLoadOrder(preparedCandidates, errors);
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry;
        try
        {
            sharedAssemblyRegistry = new RuntimeSharedAssemblyRegistry(orderedPackages.Select(x => x.LibraryFolder));
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            TryDeleteDirectory(sessionFolder);
            return new DevPackageLoadSessionResult(null, warnings.ToArray(), errors.ToArray());
        }

        var loadedPackages = new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase);
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

            var activation = await TryActivatePackageAsync(preparedPackage, sharedAssemblyRegistry, extensionCatalog, warnings, errors);
            if (!activation.Success)
            {
                sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
                continue;
            }

            loadedPackageIds.Add(preparedPackage.PackageId);
            loadedPackages[preparedPackage.PackageId] = activation.LoadedPackage!;
            sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
        }

        var session = new ActiveDevPackageSession(sessionFolder, loadedPackages, sessionPackages, extensionCatalog);
        return new DevPackageLoadSessionResult(session, warnings.ToArray(), errors.ToArray());
    }

    private static PreparedDevPackage? PreparePackage(
        int index,
        string folder,
        string sessionFolder,
        DevSessionFileMaterializer fileMaterializer,
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
            new PackageSourceDescriptor(string.Empty, PackageSourceKind.Dev, folder),
            shadowFolder,
            errors);
    }

    private static PreparedDevPackage? PrepareInstalledPackage(
        int index,
        InstalledPackageRecord package,
        string sessionFolder,
        DevSessionFileMaterializer fileMaterializer,
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
            new PackageSourceDescriptor(package.PackageId, PackageSourceKind.Installed, package.InstallPath),
            shadowFolder,
            errors);
    }

    private static PreparedDevPackage? PrepareMaterializedPackage(
        string sourceFolder,
        PackageSourceDescriptor source,
        string shadowFolder,
        ICollection<string> errors)
    {

        var shadowManifestPath = Path.Combine(shadowFolder, "sunder-package.json");
        DevPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DevPackageManifest>(File.ReadAllText(shadowManifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse '{shadowManifestPath}': {ex.Message}");
            return null;
        }

        var validationErrors = DevPackageManifestValidator.Validate(manifest, shadowFolder).ToArray();
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

        return new PreparedDevPackage(
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

    private static DevPackageManifest ToManifest(InstalledPackageRecord package)
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
                .Select(dependency => new DevPackageDependencyManifest
                {
                    PackageId = dependency.PackageId,
                    VersionRange = dependency.VersionRange,
                })
                .ToArray(),
        };

    private async Task<PackageActivationResult> TryActivatePackageAsync(
        PreparedDevPackage preparedPackage,
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry,
        RuntimePackageExtensionCatalog extensionCatalog,
        ICollection<string> warnings,
        ICollection<string> errors
    )
    {
        ActiveDevPackageLoadContext? loadContext = null;
        ServiceProvider? serviceProvider = null;
        var startedBackgroundServices = new List<IPackageBackgroundService>();

        try
        {
            loadContext = new ActiveDevPackageLoadContext(preparedPackage.PackageId, preparedPackage.EntryAssemblyPath, sharedAssemblyRegistry);
            var entryAssembly = loadContext.LoadPackageEntryAssembly();
            var moduleType = ResolvePackageModuleType(entryAssembly, out var moduleResolutionError);
            if (moduleType is null)
            {
                errors.Add($"Dev package '{preparedPackage.PackageId}' {moduleResolutionError}");
                loadContext.Unload();
                return new PackageActivationResult(false, null, BuildSessionDescriptor(
                    preparedPackage.Manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: moduleResolutionError,
                    failureCount: 1));
            }

            if (Activator.CreateInstance(moduleType) is not ISunderPackageModule module)
            {
                errors.Add($"Dev package '{preparedPackage.PackageId}' module '{moduleType.FullName}' does not implement ISunderPackageModule.");
                loadContext.Unload();
                return new PackageActivationResult(false, null, BuildSessionDescriptor(
                    preparedPackage.Manifest,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: $"Module '{moduleType.FullName}' does not implement ISunderPackageModule.",
                    failureCount: 1));
            }

            var packageContext = new DevPackageContext(preparedPackage.PackageId, preparedPackage.Version, preparedPackage.ShadowFolder);
            var services = new ServiceCollection();
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(EmptyPackageShellViewService.Instance);
            services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);

            module.ConfigureServices(services, packageContext);
            serviceProvider = services.BuildServiceProvider();

            var contributionRegistry = new CollectingPackageContributionRegistry(serviceProvider, extensionCatalog, preparedPackage.PackageId);
            module.RegisterContributions(contributionRegistry, serviceProvider);

            foreach (var backgroundService in contributionRegistry.BackgroundServices)
            {
                await backgroundService.StartAsync(CancellationToken.None);
                startedBackgroundServices.Add(backgroundService);
            }

            var loadedPackage = new ActiveLoadedDevPackage(
                BuildDescriptor(preparedPackage.Manifest, isEnabled: true, readiness: PackageReadinessState.Ready, contributionRegistry.PackageViews),
                preparedPackage.Source,
                ToProtocolConfigurationSchema(contributionRegistry.ConfigurationSchema),
                packageContext.Storage.State,
                packageContext.SecretsStore,
                serviceProvider.GetService<IPackageAuthHandler>(),
                contributionRegistry.BackgroundServices,
                serviceProvider,
                loadContext
            );
            var sessionPackage = BuildSessionDescriptor(
                preparedPackage.Manifest,
                isEnabled: true,
                readiness: PackageReadinessState.Ready,
                contributionRegistry.PackageViews);

            if (!contributionRegistry.HasRegisteredViews
                && !contributionRegistry.HasRegisteredExtensions
                && !contributionRegistry.HasRegisteredBackgroundServices
                && contributionRegistry.ConfigurationSchema is null)
            {
                warnings.Add($"Dev package '{preparedPackage.PackageId}' loaded without any package views or extension contributions.");
            }

            return new PackageActivationResult(true, loadedPackage, sessionPackage);
        }
        catch (Exception ex)
        {
            await DevPackageLifecycle.StopBackgroundServicesAsync(startedBackgroundServices, preparedPackage.PackageId, logger);
            if (serviceProvider is not null)
            {
                await DevPackageLifecycle.DisposeOwnedServiceProviderAsync(serviceProvider);
            }
            loadContext?.Unload();
            extensionCatalog.RemovePackage(preparedPackage.PackageId);
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
        var moduleTypes = entryAssembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                && typeof(ISunderPackageModule).IsAssignableFrom(type))
            .ToArray();

        if (moduleTypes.Length == 0)
        {
            error = "does not contain a public ISunderPackageModule implementation.";
            return null;
        }

        if (moduleTypes.Length > 1)
        {
            error = "contains multiple public ISunderPackageModule implementations: "
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
