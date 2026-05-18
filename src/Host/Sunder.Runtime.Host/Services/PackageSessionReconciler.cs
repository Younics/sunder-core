using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    ILogger logger,
    InstalledPackageStore installedPackageStore)
{
    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        bool startBackgroundServices,
        CancellationToken cancellationToken = default)
    {
        var installedPackages = await installedPackageStore.ListAsync(cancellationToken);
        var devFolders = devOverlays.Select(overlay => overlay.Folder).ToArray();
        if (installedPackages.Count == 0 && devFolders.Length == 0)
        {
            return new PackageSessionLoadResult(ActivePackageSession.Empty, [], []);
        }

        return devFolders.Length == 0
            ? await new PackageSessionLoadService(logger).LoadInstalledAsync(installedPackages, startBackgroundServices)
            : await new PackageSessionLoadService(logger).LoadInstalledWithDevOverlaysAsync(installedPackages, devFolders, startBackgroundServices);
    }
}
