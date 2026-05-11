using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class PackageViewHostFaultEventArgs(string packageId, string message, PackageFailureOrigin origin) : EventArgs
{
    public string PackageId { get; } = packageId;

    public string Message { get; } = message;

    public PackageFailureOrigin Origin { get; } = origin;
}

public sealed record PackageSettingsViewDescriptor(
    string PackageId,
    string DisplayName,
    string? Summary);

public sealed class PackageViewHostService : IAsyncDisposable
{
    public static PackageViewHostService Empty { get; } = new(
        new AppPackageViewRegistry(),
        new AppPackageBackgroundServiceCoordinator(),
        [],
        [],
        [],
        faultReporter: null,
        sessionFolder: null);

    private readonly AppPackageViewRegistry _viewRegistry;
    private readonly Dictionary<Assembly, string> _assemblyPackageMap = [];
    private readonly Dictionary<AssemblyLoadContext, string> _loadContextPackageMap = [];
    private readonly AppPackageBackgroundServiceCoordinator _backgroundServices;
    private readonly HashSet<string> _disabledPackageIds;
    private readonly List<object> _ownedDisposables;
    private readonly List<AppPackageLoadContext> _loadContexts;
    private readonly PackageRuntimeFaultReporter? _faultReporter;
    private readonly string? _sessionFolder;
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry;
    private readonly AppPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageShellViewService? _shellViewService;
    private readonly NotificationCenterService? _notificationCenter;
    private readonly Dictionary<string, AppLoadedPackageHandle> _loadedPackages = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal PackageViewHostService(
        AppPackageViewRegistry viewRegistry,
        AppPackageBackgroundServiceCoordinator backgroundServices,
        HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts,
        PackageRuntimeFaultReporter? faultReporter,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        AppPackageExtensionCatalog? extensionCatalog = null,
        IPackageShellViewService? shellViewService = null,
        NotificationCenterService? notificationCenter = null)
    {
        _viewRegistry = viewRegistry;
        _backgroundServices = backgroundServices;
        _disabledPackageIds = disabledPackageIds;
        _ownedDisposables = ownedDisposables.ToList();
        _loadContexts = loadContexts.ToList();
        _faultReporter = faultReporter;
        _sessionFolder = sessionFolder;
        _sharedAssemblyRegistry = sharedAssemblyRegistry ?? new AppSharedAssemblyRegistry([]);
        _extensionCatalog = extensionCatalog ?? new AppPackageExtensionCatalog();
        _shellViewService = shellViewService;
        _notificationCenter = notificationCenter;
    }

    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted;

    public static async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter = null,
        IPackageShellViewService? shellViewService = null,
        NotificationCenterService? notificationCenter = null,
        CancellationToken cancellationToken = default)
    {
        AppPackageSessionDirectories.CleanupStaleSessions();
        var sessionFolder = activePackages.Count > 0 ? AppPackageSessionDirectories.CreateSessionFolder() : null;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter,
            sessionFolder,
            new AppSharedAssemblyRegistry([]),
            new AppPackageExtensionCatalog(),
            shellViewService,
            notificationCenter);

        await Task.Run(
            () => hostService.ApplyPackageDeltaAsync(activePackages, packageSources, cancellationToken: cancellationToken),
            cancellationToken);
        return hostService;
    }

    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var forceReloadPackages = forceReloadPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(forceReloadPackageIds, StringComparer.OrdinalIgnoreCase);
        var activePackagesById = activePackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var sourcesByPackageId = packageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var loadedPackageId in _loadedPackages.Keys.Where(packageId => !activePackagesById.ContainsKey(packageId)).ToArray())
        {
            await UnloadPackageAsync(loadedPackageId, cancellationToken);
        }

        foreach (var activePackage in activePackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourcesByPackageId.TryGetValue(activePackage.PackageId, out var source))
            {
                await DisablePackageAsync(
                    activePackage.PackageId,
                    "Runtime did not provide a loadable app-side package source.",
                    PackageFailureOrigin.AppActivation,
                    cancellationToken: cancellationToken);
                continue;
            }

            var forceReload = forceReloadPackages.Contains(activePackage.PackageId);
            if (!forceReload
                && _loadedPackages.TryGetValue(activePackage.PackageId, out var loadedPackage)
                && IsSameLoadedPackage(loadedPackage, activePackage, source))
            {
                continue;
            }

            if (_loadedPackages.ContainsKey(activePackage.PackageId))
            {
                await UnloadPackageAsync(activePackage.PackageId, cancellationToken);
            }

            await LoadPackageAsync(activePackage, source, cancellationToken);
        }
    }

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        return activePackages
            .Where(package => !_disabledPackageIds.Contains(package.PackageId))
            .ToArray();
    }

    public bool TryHandleUnhandledException(Exception exception)
    {
        var packageId = ResolvePackageId(exception);
        if (packageId is null)
        {
            return false;
        }

        DisablePackage(
            packageId,
            $"Unhandled package UI exception: {exception.Message}",
            PackageFailureOrigin.AppUnhandledUi,
            exception);
        return true;
    }

    public Control? GetOrCreateView(string viewId)
        => _viewRegistry.GetOrCreateView(
            viewId,
            _disabledPackageIds.Contains,
            (packageId, message, exception) => DisablePackage(packageId, message, PackageFailureOrigin.AppHostedView, exception));

    public Control? ReloadView(string viewId)
    {
        ThrowIfDisposed();
        _viewRegistry.RemoveCachedView(viewId);
        return GetOrCreateView(viewId);
    }

    public async ValueTask NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = GetOrCreateView(viewId);
        if (view is null)
        {
            return;
        }

        var context = new PackageViewNavigationContext(viewId, parameters ?? new Dictionary<string, string?>());
        if (view is IPackageViewNavigationTarget viewTarget)
        {
            await viewTarget.OnNavigatedToAsync(context, cancellationToken);
            return;
        }

        if (view.DataContext is IPackageViewNavigationTarget dataContextTarget)
        {
            await dataContextTarget.OnNavigatedToAsync(context, cancellationToken);
        }
    }

    public bool HasSettingsView(string packageId)
        => _viewRegistry.HasSettingsView(packageId);

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
        => _viewRegistry.ListSettingsViewPackages(_disabledPackageIds.Contains);

    public Control? GetOrCreateSettingsView(string packageId)
        => _viewRegistry.GetOrCreateSettingsView(
            packageId,
            _disabledPackageIds.Contains,
            (failedPackageId, message, exception) => DisablePackage(failedPackageId, message, PackageFailureOrigin.AppHostedView, exception));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var packageId in _loadedPackages.Keys.ToArray())
        {
            await UnloadPackageAsync(packageId);
        }

        await _backgroundServices.StopAllAsync();
        await DisposeLegacyOwnedInstancesAsync();
        // Keep package shadows for the rest of the process; native library finalizers can run after package unload.
        GC.SuppressFinalize(this);
    }

    internal static async Task DisposeOwnedInstanceAsync(object ownedInstance)
    {
        if (ownedInstance is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (ownedInstance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal void RegisterPackageAssembly(string packageId, Assembly assembly)
    {
        _assemblyPackageMap[assembly] = packageId;
        var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
        if (loadContext is not null)
        {
            _loadContextPackageMap[loadContext] = packageId;
        }
    }

    internal void DisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null)
        => _ = DisablePackageAsync(packageId, message, origin, exception);

    internal async Task DisablePackageAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryMarkPackageDisabled(packageId, message, origin, exception))
        {
            return;
        }

        await _backgroundServices.StopAsync(packageId, cancellationToken);
    }

    private async Task LoadPackageAsync(
        ActivePackageDescriptor package,
        PackageSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preparedSource = PreparePackageSource(_loadedPackages.Count, source);
        if (preparedSource is null)
        {
            await DisablePackageAsync(
                package.PackageId,
                $"Failed to prepare app-side package source '{source.Folder}'.",
                PackageFailureOrigin.AppActivation,
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.Equals(preparedSource.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(preparedSource.Folder);
            await DisablePackageAsync(
                package.PackageId,
                $"Runtime package source '{source.Folder}' resolved to package '{preparedSource.PackageId}'.",
                PackageFailureOrigin.AppActivation,
                cancellationToken: cancellationToken);
            return;
        }

        ServiceProvider? serviceProvider = null;
        AppPackageLoadContext? loadContext = null;
        var packageActivated = false;
        try
        {
            var manifest = AppPackageManifest.Load(Path.Combine(preparedSource.Folder, "sunder-package.json"));
            if (manifest?.EntryAssembly is null)
            {
                throw new InvalidOperationException("App-side package manifest is missing entryAssembly.");
            }

            var packageInfo = new AppLoadedPackageInfo(package, preparedSource.Folder, manifest);
            _sharedAssemblyRegistry.AddProbeDirectories([packageInfo.LibraryFolder]);

            loadContext = new AppPackageLoadContext(package.PackageId, packageInfo.EntryAssemblyPath, _sharedAssemblyRegistry, RegisterPackageAssembly);
            _loadContexts.Add(loadContext);
            var entryAssembly = loadContext.LoadPackageEntryAssembly();
            var moduleType = ResolvePackageModuleType(entryAssembly, out var moduleResolutionError);
            if (moduleType is null)
            {
                throw new InvalidOperationException(moduleResolutionError);
            }

            if (Activator.CreateInstance(moduleType) is not ISunderPackageModule module)
            {
                throw new InvalidOperationException($"Package module '{moduleType.FullName}' does not implement ISunderPackageModule.");
            }

            var packageContext = new AppPackageContext(package.PackageId, package.Version, packageInfo.Folder);
            var services = new ServiceCollection();
            services.AddSingleton<IPackageContext>(packageContext);
            services.AddSingleton<ILoggerFactory>(packageContext.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IPackageExtensionCatalog>(_extensionCatalog);
            services.AddSingleton<IPackageShellViewService>(_shellViewService ?? DisabledPackageShellViewService.Instance);
            services.AddSingleton<IPackageNotificationService>(_notificationCenter is null
                ? NullPackageNotificationService.Instance
                : new AppPackageNotificationService(_notificationCenter, package.PackageId, package.DisplayName));
            module.ConfigureServices(services, packageContext);
            serviceProvider = services.BuildServiceProvider();
            _ownedDisposables.Add(serviceProvider);

            _viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
                package.PackageId,
                package.DisplayName,
                $"Configure {package.DisplayName}."));
            var registry = new AppPackageContributionRegistry(serviceProvider, _viewRegistry, _backgroundServices, _extensionCatalog, package.PackageId);
            module.RegisterContributions(registry, serviceProvider);
            await _backgroundServices.StartAsync(package.PackageId, cancellationToken);

            _disabledPackageIds.Remove(package.PackageId);
            _loadedPackages[package.PackageId] = new AppLoadedPackageHandle(package, source, preparedSource.Folder, serviceProvider, loadContext);
            packageActivated = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DisablePackageAsync(
                package.PackageId,
                $"Failed to activate package views: {ex.Message}",
                PackageFailureOrigin.AppActivation,
                ex,
                CancellationToken.None);
        }
        finally
        {
            if (!packageActivated)
            {
                if (serviceProvider is not null)
                {
                    _ownedDisposables.Remove(serviceProvider);
                    await TryDisposeOwnedInstanceAsync(serviceProvider, $"Failed to dispose app-side services for package '{package.PackageId}'.");
                }

                if (loadContext is not null)
                {
                    _loadContexts.Remove(loadContext);
                    TryUnloadLoadContext(loadContext, package.PackageId);
                }
            }
        }
    }

    private async Task UnloadPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (!_loadedPackages.Remove(packageId, out var handle))
        {
            return;
        }

        _disabledPackageIds.Remove(packageId);
        _viewRegistry.UnregisterPackage(packageId);
        _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageDeactivated);
        await _backgroundServices.StopAsync(packageId, cancellationToken);

        _ownedDisposables.Remove(handle.ServiceProvider);
        await TryDisposeOwnedInstanceAsync(handle.ServiceProvider, $"Failed to dispose app-side services for package '{packageId}'.");
        _loadContexts.Remove(handle.LoadContext);
        RemovePackageAssemblyMappings(packageId);
        TryUnloadLoadContext(handle.LoadContext, packageId);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private bool TryMarkPackageDisabled(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        if (!_disabledPackageIds.Add(packageId))
        {
            return false;
        }

        AppSessionLog.WriteError($"Disabled package '{packageId}' for the current app session. {message}", exception);
        _faultReporter?.ReportPackageFault(packageId, origin, message);

        _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageFaulted);
        _viewRegistry.RemoveCachedViews(packageId);

        var args = new PackageViewHostFaultEventArgs(packageId, message, origin);
        if (Dispatcher.UIThread.CheckAccess())
        {
            PackageFaulted?.Invoke(this, args);
            return true;
        }

        Dispatcher.UIThread.Post(() => PackageFaulted?.Invoke(this, args));
        return true;
    }

    private async Task DisposeLegacyOwnedInstancesAsync()
    {
        foreach (var disposable in _ownedDisposables.ToArray())
        {
            await TryDisposeOwnedInstanceAsync(disposable, "Failed to dispose an app-side package service container.");
        }

        foreach (var loadContext in _loadContexts.ToArray())
        {
            TryUnloadLoadContext(loadContext, packageId: null);
        }

        if (_loadContexts.Count > 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task TryDisposeOwnedInstanceAsync(object ownedInstance, string message)
    {
        try
        {
            await DisposeOwnedInstanceAsync(ownedInstance);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(message, ex);
        }
    }

    private static void TryUnloadLoadContext(AppPackageLoadContext loadContext, string? packageId)
    {
        try
        {
            loadContext.Unload();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(packageId is null
                ? "Failed to unload an app-side package load context."
                : $"Failed to unload app-side package load context for '{packageId}'.", ex);
        }
    }

    private void RemovePackageAssemblyMappings(string packageId)
    {
        foreach (var assembly in _assemblyPackageMap.Where(entry => string.Equals(entry.Value, packageId, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
        {
            _assemblyPackageMap.Remove(assembly);
        }

        foreach (var loadContext in _loadContextPackageMap.Where(entry => string.Equals(entry.Value, packageId, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
        {
            _loadContextPackageMap.Remove(loadContext);
        }
    }

    private string? ResolvePackageId(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var assembly = current.TargetSite?.DeclaringType?.Assembly;
            if (assembly is null)
            {
                continue;
            }

            if (_assemblyPackageMap.TryGetValue(assembly, out var packageId))
            {
                return packageId;
            }

            var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext is not null && _loadContextPackageMap.TryGetValue(loadContext, out packageId))
            {
                return packageId;
            }
        }

        return null;
    }

    private AppPreparedPackageSource? PreparePackageSource(int index, PackageSourceDescriptor source)
    {
        if (string.IsNullOrWhiteSpace(source.Folder) || !Directory.Exists(source.Folder))
        {
            return null;
        }

        var shadowRoot = _sessionFolder ?? AppPackageSessionDirectories.CreateSessionFolder();
        Directory.CreateDirectory(shadowRoot);
        var shadowFolder = Path.Combine(shadowRoot, $"{index:D2}-{SanitizeFolderName(source.PackageId)}");
        Directory.CreateDirectory(shadowFolder);
        switch (source.Kind)
        {
            case PackageSourceKind.Dev:
                CopyDirectory(source.Folder, shadowFolder);
                break;
            case PackageSourceKind.Installed:
                PrepareInstalledPackageSource(source.Folder, shadowFolder);
                break;
            default:
                TryDeleteDirectory(shadowFolder);
                return null;
        }

        var manifestPath = Path.Combine(shadowFolder, "sunder-package.json");
        if (!File.Exists(manifestPath))
        {
            TryDeleteDirectory(shadowFolder);
            return null;
        }

        var manifest = AppPackageManifest.Load(manifestPath);
        return string.IsNullOrWhiteSpace(manifest?.Id)
            ? null
            : new AppPreparedPackageSource(manifest.Id, shadowFolder);
    }

    private static void PrepareInstalledPackageSource(string sourceFolder, string shadowFolder)
    {
        var manifestPath = ResolveInstalledPackageManifestPath(sourceFolder);
        if (File.Exists(manifestPath))
        {
            File.Copy(manifestPath, Path.Combine(shadowFolder, "sunder-package.json"), overwrite: true);
        }

        var libraryFolder = ResolveInstalledPackageFolder(sourceFolder, "lib");
        if (Directory.Exists(libraryFolder))
        {
            CopyDirectory(libraryFolder, Path.Combine(shadowFolder, "lib"));
        }

        var assetFolder = ResolveInstalledPackageFolder(sourceFolder, "assets");
        if (Directory.Exists(assetFolder))
        {
            CopyDirectory(assetFolder, Path.Combine(shadowFolder, "assets"));
        }
    }

    private static string ResolveInstalledPackageManifestPath(string sourceFolder)
    {
        var packagedManifestPath = Path.Combine(sourceFolder, "manifest", "sunder-package.json");
        return File.Exists(packagedManifestPath)
            ? packagedManifestPath
            : Path.Combine(sourceFolder, "sunder-package.json");
    }

    private static string ResolveInstalledPackageFolder(string sourceFolder, string folderName)
    {
        var packagedFolder = Path.Combine(sourceFolder, "payload", folderName);
        return Directory.Exists(packagedFolder)
            ? packagedFolder
            : Path.Combine(sourceFolder, folderName);
    }

    private static void CopyDirectory(string sourceFolder, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, sourceFile);
            var destinationPath = Path.Combine(destinationFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourceFile, destinationPath, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete an app package session folder.", ex);
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

    private static Type? ResolvePackageModuleType(Assembly entryAssembly, out string? error)
    {
        var moduleTypes = entryAssembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                && typeof(ISunderPackageModule).IsAssignableFrom(type))
            .ToArray();

        if (moduleTypes.Length == 0)
        {
            error = "Package entry assembly does not contain a public ISunderPackageModule implementation.";
            return null;
        }

        if (moduleTypes.Length > 1)
        {
            error = "Package entry assembly contains multiple public ISunderPackageModule implementations: "
                + string.Join(", ", moduleTypes.Select(static type => type.FullName));
            return null;
        }

        var moduleType = moduleTypes[0];
        if (moduleType.GetConstructor(Type.EmptyTypes) is null)
        {
            error = $"Package module '{moduleType.FullName}' must declare a public parameterless constructor.";
            return null;
        }

        error = null;
        return moduleType;
    }

    private static bool IsSameLoadedPackage(
        AppLoadedPackageHandle loadedPackage,
        ActivePackageDescriptor activePackage,
        PackageSourceDescriptor source)
        => string.Equals(loadedPackage.Package.Version, activePackage.Version, StringComparison.OrdinalIgnoreCase)
           && loadedPackage.Source.Kind == source.Kind
           && string.Equals(loadedPackage.Source.Folder, source.Folder, StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PackageViewHostService));
        }
    }
}
