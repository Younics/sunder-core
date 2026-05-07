using Microsoft.Extensions.Logging;
using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageSessionState(
    ILogger logger,
    Action clearAuthSessions,
    Action<string> removePackageAuthSessions)
{
    private readonly object _syncRoot = new();
    private ActiveDevPackageSession _activeSession = ActiveDevPackageSession.Empty;

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        lock (_syncRoot)
        {
            return _activeSession.GetActivePackages();
        }
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        lock (_syncRoot)
        {
            return _activeSession.GetSessionPackages();
        }
    }

    public IReadOnlyList<PackageSourceDescriptor> GetActivePackageSources()
    {
        lock (_syncRoot)
        {
            return _activeSession.GetActivePackageSources();
        }
    }

    public IReadOnlyList<ActiveLoadedDevPackage> ListEnabledLoadedPackages()
    {
        lock (_syncRoot)
        {
            return _activeSession.LoadedPackageMap.Values
                .Where(package => _activeSession.IsPackageEnabled(package.Descriptor.PackageId))
                .ToArray();
        }
    }

    public bool ReportPackageFault(string packageId, ReportPackageFaultRequest request)
    {
        ActiveLoadedDevPackage? packageToDeactivate;
        lock (_syncRoot)
        {
            var disabled = _activeSession.MarkPackageFailed(packageId, request.Origin, request.Message, out packageToDeactivate);
            if (!disabled)
            {
                return false;
            }

            removePackageAuthSessions(packageId);

            logger.LogError(
                "Disabled package {PackageId} for the current session after {Origin}: {Message}",
                packageId,
                request.Origin,
                request.Message);
        }

        QueuePackageDeactivation(packageId, packageToDeactivate);
        return true;
    }

    public async Task<IReadOnlyList<string>> ClearActiveSessionAsync()
    {
        ActiveDevPackageSession previousSession;
        lock (_syncRoot)
        {
            previousSession = _activeSession;
            _activeSession = ActiveDevPackageSession.Empty;
            clearAuthSessions();
        }

        if (ReferenceEquals(previousSession, ActiveDevPackageSession.Empty))
        {
            return [];
        }

        var warnings = new List<string>();

        try
        {
            await previousSession.StopBackgroundServicesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop background services while unloading the previous dev-package session");
            warnings.Add($"Previous dev-package background services did not stop cleanly: {ex.Message}");
        }

        try
        {
            await previousSession.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose the previous dev-package session cleanly");
            warnings.Add($"Previous dev-package session cleanup failed: {ex.Message}");
        }

        return warnings;
    }

    public void PublishSession(ActiveDevPackageSession session)
    {
        lock (_syncRoot)
        {
            _activeSession = session;
            clearAuthSessions();
        }
    }

    public void HandlePackageFault(string packageId, PackageFailureOrigin origin, Exception exception, string action)
    {
        ActiveLoadedDevPackage? packageToDeactivate;
        lock (_syncRoot)
        {
            _activeSession.MarkPackageFailed(packageId, origin, exception.Message, out packageToDeactivate);

            removePackageAuthSessions(packageId);
        }

        QueuePackageDeactivation(packageId, packageToDeactivate);
        logger.LogError(exception, "Failed to {Action} for package {PackageId}; package disabled for current session", action, packageId);
    }

    public bool DisableInstalledPackage(string packageId)
    {
        ActiveLoadedDevPackage? packageToDeactivate;
        lock (_syncRoot)
        {
            if (!_activeSession.DisableInstalledPackage(packageId, out packageToDeactivate))
            {
                return false;
            }

            removePackageAuthSessions(packageId);
        }

        QueuePackageDeactivation(packageId, packageToDeactivate);
        return true;
    }

    public bool RemovePackage(string packageId)
    {
        ActiveLoadedDevPackage? packageToDeactivate;
        lock (_syncRoot)
        {
            if (!_activeSession.RemovePackage(packageId, out packageToDeactivate))
            {
                return false;
            }

            removePackageAuthSessions(packageId);
        }

        QueuePackageDeactivation(packageId, packageToDeactivate);
        return true;
    }

    public ActiveLoadedDevPackage? GetLoadedPackage(string packageId)
    {
        lock (_syncRoot)
        {
            return _activeSession.TryGetLoadedPackage(packageId, out var loadedPackage) ? loadedPackage : null;
        }
    }

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        lock (_syncRoot)
        {
            return _activeSession.TryResolvePackageAssetPath(packageId, assetPath);
        }
    }

    private void QueuePackageDeactivation(string packageId, ActiveLoadedDevPackage? loadedPackage)
    {
        if (loadedPackage is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DeactivateLoadedPackageAsync(packageId, loadedPackage);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deactivate package {PackageId} after a package fault", packageId);
            }
        });
    }

    private async Task DeactivateLoadedPackageAsync(string packageId, ActiveLoadedDevPackage loadedPackage)
    {
        await DevPackageLifecycle.StopBackgroundServicesAsync(loadedPackage.BackgroundServices, packageId, logger);
        await DevPackageLifecycle.DisposeOwnedServiceProviderAsync(loadedPackage.ServiceProvider);
        loadedPackage.LoadContext.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
