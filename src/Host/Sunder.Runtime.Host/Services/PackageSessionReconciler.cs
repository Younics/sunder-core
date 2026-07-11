using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    ILogger logger,
    InstalledPackageStore installedPackageStore,
    RuntimePackagePaths paths)
{
    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        bool startBackgroundServices,
        CancellationToken cancellationToken = default)
        => await LoadMergedSessionAsync(await installedPackageStore.ListAsync(cancellationToken), devOverlays, startBackgroundServices, cancellationToken);

    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        bool startBackgroundServices,
        CancellationToken cancellationToken = default)
    {
        var devFolders = devOverlays.Select(overlay => overlay.Folder).ToArray();
        if (installedPackages.Count == 0 && devFolders.Length == 0)
        {
            return new PackageSessionLoadResult(ActivePackageSession.Empty, [], []);
        }

        return devFolders.Length == 0
            ? await new PackageSessionLoadService(logger, paths).LoadInstalledAsync(installedPackages, startBackgroundServices)
            : await new PackageSessionLoadService(logger, paths).LoadInstalledWithDevOverlaysAsync(installedPackages, devFolders, startBackgroundServices);
    }
}
