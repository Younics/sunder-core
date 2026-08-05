using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    InstalledPackageStore installedPackageStore,
    PackageSessionLoadService loader)
{
    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        CancellationToken cancellationToken = default)
        => await LoadMergedSessionAsync(
            await installedPackageStore.ListAsync(cancellationToken),
            devOverlays,
            cancellationToken: cancellationToken);

    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        IReadOnlyDictionary<string, string>? preparationSourcePaths = null,
        CancellationToken cancellationToken = default)
    {
        var devFolders = devOverlays.Select(overlay => overlay.Folder).ToArray();
        if (installedPackages.Count == 0 && devFolders.Length == 0)
        {
            return new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(), [], []);
        }

        return devFolders.Length == 0
            ? await loader.LoadInstalledAsync(installedPackages, preparationSourcePaths, cancellationToken)
            : await loader.LoadInstalledWithDevOverlaysAsync(installedPackages, devFolders, preparationSourcePaths, cancellationToken);
    }
}
