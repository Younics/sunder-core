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

        var plannedActions = new List<AppPackageDeltaPlanAction>();
        foreach (var activePackage in activePackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!deltaPlan.TryGetSource(activePackage, out var source))
            {
                plannedActions.Add(new AppPackageDeltaPlanAction(activePackage, null, AppPackageDeltaAction.MissingSource));
                continue;
            }

            var loadedPackage = getLoadedPackage(activePackage.PackageId);
            var action = deltaPlan.GetAction(activePackage, source, loadedPackage, isPackageDisabled(activePackage.PackageId));
            plannedActions.Add(new AppPackageDeltaPlanAction(activePackage, source, action));
        }

        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (plannedAction.Action)
            {
                case AppPackageDeltaAction.UnloadDisabled:
                    await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, true);
                    break;
                case AppPackageDeltaAction.Reload:
                    await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, false);
                    break;
            }
        }

        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (plannedAction.Action)
            {
                case AppPackageDeltaAction.MissingSource:
                    await disablePackageAsync(
                        plannedAction.Package.PackageId,
                        "Runtime did not provide a loadable app-side package source.",
                        PackageFailureOrigin.AppActivation,
                        null,
                        cancellationToken);
                    break;
                case AppPackageDeltaAction.SkipDisabled:
                case AppPackageDeltaAction.SkipLoaded:
                case AppPackageDeltaAction.UnloadDisabled:
                    break;
                case AppPackageDeltaAction.Reload:
                    await loadPackageAsync(plannedAction.Package, plannedAction.Source!, cancellationToken);
                    break;
                case AppPackageDeltaAction.Load:
                    await loadPackageAsync(plannedAction.Package, plannedAction.Source!, cancellationToken);
                    break;
            }
        }
    }

    private sealed record AppPackageDeltaPlanAction(
        ActivePackageDescriptor Package,
        PackageSourceDescriptor? Source,
        AppPackageDeltaAction Action);
}
