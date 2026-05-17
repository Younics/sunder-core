using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageDeltaCoordinator(
    Func<Func<string, bool>, string[]> snapshotLoadedPackageIds,
    Func<string, AppLoadedPackageHandle?> getLoadedPackage,
    Func<string, bool> isPackageDisabled,
    Func<string, CancellationToken, bool, Task<bool>> unloadPackageAsync,
    Func<ActivePackageDescriptor, PackageSourceDescriptor, CancellationToken, Task> loadPackageAsync,
    Func<string, string, PackageFailureOrigin, Exception?, CancellationToken, Task> disablePackageAsync)
{
    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
    {
        var deltaPlan = new AppPackageDeltaPlan(activePackages, packageSources, forceReloadPackageIds);
        var loadedPackageIds = snapshotLoadedPackageIds(deltaPlan.IsPackageInactive);

        foreach (var loadedPackageId in loadedPackageIds)
        {
            await unloadPackageAsync(loadedPackageId, cancellationToken, false);
        }

        foreach (var activePackage in activePackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!deltaPlan.TryGetSource(activePackage, out var source))
            {
                await disablePackageAsync(
                    activePackage.PackageId,
                    "Runtime did not provide a loadable app-side package source.",
                    PackageFailureOrigin.AppActivation,
                    null,
                    cancellationToken);
                continue;
            }

            var loadedPackage = getLoadedPackage(activePackage.PackageId);
            switch (deltaPlan.GetAction(activePackage, source, loadedPackage, isPackageDisabled(activePackage.PackageId)))
            {
                case AppPackageDeltaAction.UnloadDisabled:
                    await unloadPackageAsync(activePackage.PackageId, cancellationToken, true);
                    break;
                case AppPackageDeltaAction.SkipDisabled:
                case AppPackageDeltaAction.SkipLoaded:
                    break;
                case AppPackageDeltaAction.Reload:
                    await unloadPackageAsync(activePackage.PackageId, cancellationToken, false);
                    await loadPackageAsync(activePackage, source, cancellationToken);
                    break;
                case AppPackageDeltaAction.Load:
                    await loadPackageAsync(activePackage, source, cancellationToken);
                    break;
            }
        }
    }
}
