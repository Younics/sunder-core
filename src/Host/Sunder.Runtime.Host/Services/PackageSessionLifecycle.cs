using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal static class PackageSessionLifecycle
{
    public static async Task StopBackgroundServicesAsync(
        IReadOnlyList<IPackageBackgroundService> backgroundServices,
        string packageId,
        ILogger logger)
    {
        foreach (var backgroundService in backgroundServices)
        {
            try
            {
                await backgroundService.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop background service while rolling back package {PackageId}", packageId);
            }
        }
    }

    public static async Task DisposeOwnedServiceProviderAsync(IServiceProvider serviceProvider)
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
}
