using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed record ActiveLoadedDevPackage(
    ActivePackageDescriptor Descriptor,
    PackageSourceDescriptor Source,
    PackageConfigurationSchemaDescriptor? ConfigurationSchema,
    IPackageKeyValueStore StateStore,
    JsonPackageSecretsStore SecretsStore,
    IPackageAuthHandler? AuthHandler,
    IReadOnlyDictionary<string, IPackageCallbackHandler> CallbackHandlers,
    IReadOnlyList<IPackageBackgroundService> BackgroundServices,
    IServiceProvider ServiceProvider,
    ActiveDevPackageLoadContext LoadContext)
{
    public IReadOnlyList<string> SecretKeys => SecretsStore.ListKeys();

    public IPackageCallbackHandler? GetCallbackHandler(string callbackHandlerId)
        => CallbackHandlers.TryGetValue(callbackHandlerId, out var handler) ? handler : null;
}

internal sealed class ActiveDevPackageSession
{
    private readonly Dictionary<string, ActiveLoadedDevPackage> _loadedPackageMap;
    private readonly Dictionary<string, SessionPackageDescriptor> _sessionPackageMap;
    private readonly RuntimePackageExtensionCatalog _extensionCatalog;

    public ActiveDevPackageSession(
        string? sessionFolder,
        IDictionary<string, ActiveLoadedDevPackage> loadedPackages,
        IDictionary<string, SessionPackageDescriptor> sessionPackages,
        RuntimePackageExtensionCatalog? extensionCatalog = null)
    {
        SessionFolder = sessionFolder;
        _loadedPackageMap = new Dictionary<string, ActiveLoadedDevPackage>(loadedPackages, StringComparer.OrdinalIgnoreCase);
        _sessionPackageMap = new Dictionary<string, SessionPackageDescriptor>(sessionPackages, StringComparer.OrdinalIgnoreCase);
        _extensionCatalog = extensionCatalog ?? new RuntimePackageExtensionCatalog();
    }

    public static ActiveDevPackageSession Empty { get; } = new(
        null,
        new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
    );

    public string? SessionFolder { get; }

    public IReadOnlyDictionary<string, ActiveLoadedDevPackage> LoadedPackageMap => _loadedPackageMap;

    public IReadOnlyDictionary<string, SessionPackageDescriptor> SessionPackageMap => _sessionPackageMap;

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        return _sessionPackageMap.Values
            .Where(package => package.IsEnabled)
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(ToActiveDescriptor)
            .ToArray();
    }

    public IReadOnlyList<PackageSourceDescriptor> GetActivePackageSources()
        => _loadedPackageMap.Values
            .Where(package => IsPackageEnabled(package.Descriptor.PackageId))
            .OrderBy(package => package.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(package => package.Source)
            .ToArray();

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        return _sessionPackageMap.Values
            .OrderByDescending(package => !package.IsEnabled)
            .ThenBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsPackageEnabled(string packageId)
    {
        return _sessionPackageMap.TryGetValue(packageId, out var package)
            && package.IsEnabled;
    }

    public bool TryGetLoadedPackage(string packageId, out ActiveLoadedDevPackage? loadedPackage)
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

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        if (!_loadedPackageMap.TryGetValue(packageId, out var package)
            || !IsPackageEnabled(packageId))
        {
            return null;
        }

        return package.Source.Kind switch
        {
            PackageSourceKind.Dev => PackageAssetPathResolver.TryResolveDevAssetPath(package.Source.Folder, assetPath),
            PackageSourceKind.Installed => PackageAssetPathResolver.TryResolveInstalledAssetPath(package.Source.Folder, assetPath),
            _ => null,
        };
    }

    public bool MarkPackageFailed(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        out ActiveLoadedDevPackage? packageToDeactivate)
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

    public bool DisableInstalledPackage(string packageId, out ActiveLoadedDevPackage? packageToDeactivate)
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

    public bool RemovePackage(string packageId, out ActiveLoadedDevPackage? packageToDeactivate)
    {
        var removedSessionPackage = _sessionPackageMap.Remove(packageId);
        var removedLoadedPackage = _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        if (removedSessionPackage || removedLoadedPackage)
        {
            _extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageUninstalled);
            return true;
        }

        return false;
    }

    public async Task StopBackgroundServicesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var package in _loadedPackageMap.Values)
        {
            foreach (var backgroundService in package.BackgroundServices)
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
    }

    public async Task DisposeAsync()
    {
        List<Exception>? disposeErrors = null;

        foreach (var package in _loadedPackageMap.Values)
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

        // Keep package shadows for the rest of the process; native library finalizers can run after package unload.

        if (disposeErrors is { Count: > 0 })
        {
            throw new AggregateException(disposeErrors);
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
            package.Icon,
            package.IsEnabled,
            package.Readiness,
            package.Views);
}
