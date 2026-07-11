using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageDeltaCoordinator(
    Func<Func<string, bool>, string[]> snapshotLoadedPackageIds,
    Func<string, AppLoadedPackageHandle?> getLoadedPackage,
    Func<string, bool> isPackageDisabled,
    Func<string, CancellationToken, bool, Task<bool>> unloadPackageAsync,
    Func<ActivePackageDescriptor, PackageUiSnapshotDescriptor, CancellationToken, Task> loadPackageAsync,
    Func<string, string, PackageFailureOrigin, Exception?, CancellationToken, Task> disablePackageAsync,
    Func<IReadOnlyList<PackageUiSnapshotDescriptor>, bool>? requiresSharedAssemblyReset = null,
    Action? resetSharedAssemblies = null,
    Func<ActivePackageDescriptor, PackageUiSnapshotDescriptor, CancellationToken, Task<AppPackagePrepareResult>>? preparePackageAsync = null,
    Func<AppPreparedPackageActivation, CancellationToken, Task>? activatePreparedPackageAsync = null,
    Action<IReadOnlyList<string>>? addSharedAssemblyProbeDirectories = null)
{
    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
    {
        var sharedAssemblyResetRequired = requiresSharedAssemblyReset?.Invoke(packageSources) == true;
        var effectiveForceReloadPackageIds = sharedAssemblyResetRequired
            ? activePackages.Select(package => package.PackageId).ToArray()
            : forceReloadPackageIds;
        var deltaPlan = new AppPackageDeltaPlan(activePackages, packageSources, effectiveForceReloadPackageIds);
        var loadedPackageIds = sharedAssemblyResetRequired
            ? snapshotLoadedPackageIds(_ => true)
            : snapshotLoadedPackageIds(deltaPlan.IsPackageInactive);

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

        if (preparePackageAsync is not null && activatePreparedPackageAsync is not null && addSharedAssemblyProbeDirectories is not null)
        {
            await ApplyPreparedPackageDeltaAsync(
                plannedActions,
                loadedPackageIds,
                sharedAssemblyResetRequired,
                preparePackageAsync,
                activatePreparedPackageAsync,
                addSharedAssemblyProbeDirectories,
                cancellationToken);
            return;
        }

        var preUnloadedPackageIds = loadedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var loadedPackageId in loadedPackageIds)
        {
            await unloadPackageAsync(loadedPackageId, cancellationToken, false);
        }

        if (sharedAssemblyResetRequired)
        {
            resetSharedAssemblies?.Invoke();
        }

        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (plannedAction.Action)
            {
                case AppPackageDeltaAction.UnloadDisabled:
                    if (!preUnloadedPackageIds.Contains(plannedAction.Package.PackageId))
                    {
                        await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, true);
                    }

                    break;
                case AppPackageDeltaAction.Reload:
                    if (!preUnloadedPackageIds.Contains(plannedAction.Package.PackageId))
                    {
                        await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, false);
                    }

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

    private async Task ApplyPreparedPackageDeltaAsync(
        IReadOnlyList<AppPackageDeltaPlanAction> plannedActions,
        IReadOnlyList<string> loadedPackageIds,
        bool sharedAssemblyResetRequired,
        Func<ActivePackageDescriptor, PackageUiSnapshotDescriptor, CancellationToken, Task<AppPackagePrepareResult>> preparePackageAsync,
        Func<AppPreparedPackageActivation, CancellationToken, Task> activatePreparedPackageAsync,
        Action<IReadOnlyList<string>> addSharedAssemblyProbeDirectories,
        CancellationToken cancellationToken)
    {
        var preparedActivations = new List<AppPreparedPackageActivation>();
        var preparationFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plannedAction.Action is not (AppPackageDeltaAction.Load or AppPackageDeltaAction.Reload))
            {
                continue;
            }

            var prepareResult = await preparePackageAsync(plannedAction.Package, plannedAction.Source!, cancellationToken);
            if (prepareResult.IsSuccess && prepareResult.Activation is not null)
            {
                preparedActivations.Add(prepareResult.Activation);
                continue;
            }

            preparationFailures[plannedAction.Package.PackageId] = prepareResult.FailureMessage ?? "Failed to prepare app-side package source.";
        }

        var preUnloadedPackageIds = loadedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var loadedPackageId in loadedPackageIds)
        {
            await unloadPackageAsync(loadedPackageId, cancellationToken, false);
        }

        if (sharedAssemblyResetRequired)
        {
            resetSharedAssemblies?.Invoke();
        }

        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (plannedAction.Action)
            {
                case AppPackageDeltaAction.UnloadDisabled:
                    if (!preUnloadedPackageIds.Contains(plannedAction.Package.PackageId))
                    {
                        await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, true);
                    }

                    break;
                case AppPackageDeltaAction.Reload:
                    if (!preUnloadedPackageIds.Contains(plannedAction.Package.PackageId))
                    {
                        await unloadPackageAsync(plannedAction.Package.PackageId, cancellationToken, false);
                    }

                    break;
            }
        }

        if (preparedActivations.Count > 0)
        {
            addSharedAssemblyProbeDirectories(preparedActivations.Select(activation => activation.LibraryFolder).ToArray());
        }

        foreach (var plannedAction in plannedActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preparationFailures.TryGetValue(plannedAction.Package.PackageId, out var failureMessage))
            {
                await disablePackageAsync(plannedAction.Package.PackageId, failureMessage, PackageFailureOrigin.AppActivation, null, cancellationToken);
                continue;
            }

            if (plannedAction.Action == AppPackageDeltaAction.MissingSource)
            {
                await disablePackageAsync(
                    plannedAction.Package.PackageId,
                    "Runtime did not provide a loadable app-side package source.",
                    PackageFailureOrigin.AppActivation,
                    null,
                    cancellationToken);
                continue;
            }

            if (plannedAction.Action is not (AppPackageDeltaAction.Load or AppPackageDeltaAction.Reload))
            {
                continue;
            }

            var activation = preparedActivations.FirstOrDefault(candidate => string.Equals(candidate.Package.PackageId, plannedAction.Package.PackageId, StringComparison.OrdinalIgnoreCase));
            if (activation is not null)
            {
                await activatePreparedPackageAsync(activation, cancellationToken);
            }
        }
    }

    private sealed record AppPackageDeltaPlanAction(
        ActivePackageDescriptor Package,
        PackageUiSnapshotDescriptor? Source,
        AppPackageDeltaAction Action);
}
