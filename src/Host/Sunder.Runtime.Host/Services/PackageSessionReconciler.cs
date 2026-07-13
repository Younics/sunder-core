using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    InstalledPackageStore installedPackageStore,
    PackageSessionLoadService loader)
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
            ? await loader.LoadInstalledAsync(installedPackages, startBackgroundServices, cancellationToken)
            : await loader.LoadInstalledWithDevOverlaysAsync(installedPackages, devFolders, startBackgroundServices, cancellationToken);
    }
}
