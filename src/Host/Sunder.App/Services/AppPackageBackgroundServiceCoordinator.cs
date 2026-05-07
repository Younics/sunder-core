using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageBackgroundServiceCoordinator
{
    private readonly Dictionary<string, List<IPackageBackgroundService>> _backgroundServicesByPackageId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    public void Register(string packageId, IPackageBackgroundService service)
    {
        lock (_syncRoot)
        {
            if (!_backgroundServicesByPackageId.TryGetValue(packageId, out var services))
            {
                services = [];
                _backgroundServicesByPackageId[packageId] = services;
            }

            services.Add(service);
        }
    }

    public async Task StartAsync(string packageId, CancellationToken cancellationToken = default)
    {
        List<IPackageBackgroundService>? services;
        lock (_syncRoot)
        {
            services = _backgroundServicesByPackageId.TryGetValue(packageId, out var registeredServices)
                ? [.. registeredServices]
                : null;
        }

        if (services is null)
        {
            return;
        }

        foreach (var service in services)
        {
            await service.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(string packageId, CancellationToken cancellationToken = default)
    {
        List<IPackageBackgroundService>? services;
        lock (_syncRoot)
        {
            services = _backgroundServicesByPackageId.Remove(packageId, out var registeredServices)
                ? [.. registeredServices]
                : null;
        }

        if (services is null)
        {
            return;
        }

        foreach (var service in services)
        {
            try
            {
                await service.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError($"Failed to stop background service for package '{packageId}'.", ex);
            }
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        string[] packageIds;
        lock (_syncRoot)
        {
            packageIds = _backgroundServicesByPackageId.Keys.ToArray();
        }

        foreach (var packageId in packageIds)
        {
            await StopAsync(packageId, cancellationToken);
        }
    }
}
