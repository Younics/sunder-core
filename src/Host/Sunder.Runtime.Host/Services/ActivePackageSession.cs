using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal sealed record ActiveLoadedPackage(
    ActivePackageDescriptor Descriptor,
    RuntimePackageSource Source,
    PackageSettingsSchemaDescriptor? SettingsSchema,
    IPackageKeyValueStore StateStore,
    JsonPackageSecretsStore SecretsStore,
    IPackageAuthHandler? AuthHandler,
    IReadOnlyDictionary<string, IPackageCallbackHandler> CallbackHandlers,
    IReadOnlyList<IPackageBackgroundService> BackgroundServices,
    IServiceProvider ServiceProvider,
    RuntimePackageLoadContext LoadContext,
    IPackageSettings Settings)
{
    public PackageSettingsSchema? CanonicalSettingsSchema { get; init; }

    public IReadOnlyDictionary<string, RuntimePackageOperationRegistration> RuntimeOperations { get; init; }
        = new Dictionary<string, RuntimePackageOperationRegistration>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RuntimePackageStreamRegistration> RuntimeStreams { get; init; }
        = new Dictionary<string, RuntimePackageStreamRegistration>(StringComparer.Ordinal);

    public IPackageCallbackHandler? GetCallbackHandler(string callbackHandlerId)
        => CallbackHandlers.TryGetValue(callbackHandlerId, out var handler) ? handler : null;
}

internal sealed class ActivePackageSession
{
    private readonly Dictionary<string, ActiveLoadedPackage> _loadedPackageMap;
    private readonly Dictionary<string, SessionPackageDescriptor> _sessionPackageMap;
    private readonly Dictionary<string, RuntimePackageSource> _packageSourceMap;
    private readonly RuntimePackageExtensionCatalog _extensionCatalog;

    public ActivePackageSession(
        string? sessionFolder,
        IDictionary<string, ActiveLoadedPackage> loadedPackages,
        IDictionary<string, SessionPackageDescriptor> sessionPackages,
        RuntimePackageExtensionCatalog? extensionCatalog = null,
        RuntimeSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        bool backgroundServicesStarted = true,
        IEnumerable<RuntimePackageSource>? readySources = null)
    {
        SessionFolder = sessionFolder;
        _loadedPackageMap = new Dictionary<string, ActiveLoadedPackage>(loadedPackages, StringComparer.OrdinalIgnoreCase);
        _sessionPackageMap = new Dictionary<string, SessionPackageDescriptor>(sessionPackages, StringComparer.OrdinalIgnoreCase);
        _packageSourceMap = (readySources ?? loadedPackages.Values.Select(static package => package.Source))
            .ToDictionary(static source => source.PackageId, StringComparer.OrdinalIgnoreCase);
        _extensionCatalog = extensionCatalog ?? new RuntimePackageExtensionCatalog();
        SharedAssemblyRegistry = sharedAssemblyRegistry;
        _backgroundServicesStarted = backgroundServicesStarted;
    }

    private bool _backgroundServicesStarted;

    public static ActivePackageSession Empty { get; } = new(
        null,
        new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
    );

    public string? SessionFolder { get; }

    private RuntimeSharedAssemblyRegistry? SharedAssemblyRegistry { get; }

    public async Task StartBackgroundServicesAsync(ILogger logger, CancellationToken cancellationToken = default)
    {
        if (_backgroundServicesStarted)
        {
            return;
        }

        var startedServices = new List<(string PackageId, IPackageBackgroundService BackgroundService)>();
        try
        {
            foreach (var package in _loadedPackageMap.Values)
            {
                foreach (var backgroundService in package.BackgroundServices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await backgroundService.StartAsync(cancellationToken);
                    startedServices.Add((package.Descriptor.PackageId, backgroundService));
                }
            }

            _backgroundServicesStarted = true;
        }
        catch
        {
            foreach (var group in startedServices
                         .AsEnumerable()
                         .Reverse()
                         .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                await PackageSessionLifecycle.StopBackgroundServicesAsync(group.Select(x => x.BackgroundService).ToArray(), group.Key, logger);
            }

            throw;
        }
    }

    public IReadOnlyDictionary<string, ActiveLoadedPackage> LoadedPackageMap => _loadedPackageMap;

    public IReadOnlyDictionary<string, SessionPackageDescriptor> SessionPackageMap => _sessionPackageMap;

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        return _sessionPackageMap.Values
            .Where(package => package.IsEnabled)
            .Select(ToActiveDescriptor)
            .ToArray();
    }

    public IReadOnlyList<RuntimePackageSource> GetActivePackageSources()
        => _packageSourceMap.Values
            .Where(source => IsPackageEnabled(source.PackageId))
            .ToArray();

    public IReadOnlyList<RuntimePackageSource> GetAppPackageSources()
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(_packageSourceMap.Values
            .Where(source => IsPackageEnabled(source.PackageId) && (source.HostRoles & PackageHostRoles.App) != 0)
            .Select(static source => source.PackageId));
        while (pending.TryPop(out var packageId))
        {
            if (!required.Add(packageId) || !_packageSourceMap.TryGetValue(packageId, out var source)) continue;
            foreach (var dependency in source.PackageDependencies) pending.Push(dependency);
        }

        return _packageSourceMap.Values
            .Where(source => required.Contains(source.PackageId) && IsPackageEnabled(source.PackageId))
            .ToArray();
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        return _sessionPackageMap.Values
            .OrderByDescending(package => !package.IsEnabled)
            .ThenBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryGetSessionPackage(string packageId, out SessionPackageDescriptor? package)
        => _sessionPackageMap.TryGetValue(packageId, out package);

    public bool IsPackageEnabled(string packageId)
    {
        return _sessionPackageMap.TryGetValue(packageId, out var package)
            && package.IsEnabled;
    }

    public bool TryGetLoadedPackage(string packageId, out ActiveLoadedPackage? loadedPackage)
    {
        if (_loadedPackageMap.TryGetValue(packageId, out var package)
            && IsPackageEnabled(packageId))
        {
            loadedPackage = package;
            return true;
        }

        loadedPackage = null;
        return false;
    }

    public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
        => _extensionCatalog.GetExtensionContributions(extensionPoint);

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        if (!_packageSourceMap.TryGetValue(packageId, out var source)
            || !IsPackageEnabled(packageId))
        {
            return null;
        }

        return source.Kind switch
        {
            PackageSourceKind.Dev => PackageAssetPathResolver.TryResolveDevAssetPath(source.SourceFolder, assetPath),
            PackageSourceKind.Installed => PackageAssetPathResolver.TryResolveInstalledAssetPath(source.SourceFolder, assetPath),
            _ => null,
        };
    }

    public bool MarkPackageFailed(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        out ActiveLoadedPackage? packageToDeactivate)
    {
        packageToDeactivate = null;
        if (!_sessionPackageMap.TryGetValue(packageId, out var package))
        {
            return false;
        }

        var updatedPackage = package with
        {
            IsEnabled = false,
            Readiness = PackageReadinessState.Failed,
            FailureOrigin = origin,
            LastError = message,
            LastFailureAtUtc = DateTimeOffset.UtcNow,
            FailureCount = package.FailureCount + 1,
        };

        _sessionPackageMap[packageId] = updatedPackage;
        _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageFaulted);

        return true;
    }

    public bool DisableInstalledPackage(string packageId, out ActiveLoadedPackage? packageToDeactivate)
    {
        packageToDeactivate = null;
        if (!_sessionPackageMap.TryGetValue(packageId, out var package))
        {
            return false;
        }

        _sessionPackageMap[packageId] = package with
        {
            IsEnabled = false,
            Readiness = PackageReadinessState.Disabled,
            FailureOrigin = null,
            LastError = null,
            LastFailureAtUtc = null,
        };
        _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageDisabled);
        return true;
    }

    public bool RemovePackage(string packageId, out ActiveLoadedPackage? packageToDeactivate)
    {
        var removedSessionPackage = _sessionPackageMap.Remove(packageId);
        var removedLoadedPackage = _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        _packageSourceMap.Remove(packageId);
        if (removedSessionPackage || removedLoadedPackage)
        {
            _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageUninstalled);
            return true;
        }

        return false;
    }

    public async Task StopBackgroundServicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_backgroundServicesStarted)
        {
            return;
        }

        foreach (var package in _loadedPackageMap.Values.Reverse())
        {
            foreach (var backgroundService in package.BackgroundServices.Reverse())
            {
                try
                {
                    await backgroundService.StopAsync(cancellationToken);
                }
                catch
                {
                    // Best effort package shutdown. Remaining resources are still disposed.
                }
            }
        }

        _backgroundServicesStarted = false;
    }

    public async Task DisposeAsync()
    {
        List<Exception>? disposeErrors = null;

        foreach (var package in _loadedPackageMap.Values.Reverse())
        {
            try
            {
                await DisposeServiceProviderAsync(package.ServiceProvider);
            }
            catch (Exception ex)
            {
                disposeErrors ??= [];
                disposeErrors.Add(new InvalidOperationException(
                    $"Failed to dispose services for package '{package.Descriptor.PackageId}'.",
                    ex));
            }

            try
            {
                package.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                disposeErrors ??= [];
                disposeErrors.Add(new InvalidOperationException(
                    $"Failed to unload package load context for '{package.Descriptor.PackageId}'.",
                    ex));
            }
        }

        if (_loadedPackageMap.Count > 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        SharedAssemblyRegistry?.Dispose();

        TryDeleteSessionFolder();

        if (disposeErrors is { Count: > 0 })
        {
            throw new AggregateException(disposeErrors);
        }
    }

    private void TryDeleteSessionFolder()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SessionFolder) && Directory.Exists(SessionFolder))
            {
                Directory.Delete(SessionFolder, recursive: true);
            }
        }
        catch
        {
            // Native library finalizers may retain a shadow temporarily; stale-session GC retries later.
        }
    }

    private static async Task DisposeServiceProviderAsync(IServiceProvider serviceProvider)
    {
        if (serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public static ActivePackageDescriptor ToActiveDescriptor(SessionPackageDescriptor package)
        => new(
            package.PackageId,
            package.DisplayName,
            package.Version,
            package.HostRoles,
            package.Icon,
            package.IsEnabled,
            package.Readiness,
            package.Views);
}
