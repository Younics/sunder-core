using System.Reflection;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed record AppPackagePreflightResult(
    bool Success,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public static AppPackagePreflightResult Succeeded(IReadOnlyList<string>? warnings = null)
        => new(true, warnings ?? [], []);

    public static AppPackagePreflightResult Failed(string message, IReadOnlyList<string>? warnings = null)
        => new(false, warnings ?? [], [message]);
}

internal sealed class AppPackagePreflightCoordinator(
    Func<string, AppLoadedPackageHandle?> getLoadedPackage,
    Func<string, bool> isPackageDisabled,
    Func<IReadOnlyList<PackageUiSnapshotDescriptor>, bool>? requiresSharedAssemblyReset = null,
    Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task>? downloadSnapshotAsync = null)
{
    public async Task<AppPackagePreflightResult> PreflightPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveForceReloadPackageIds = requiresSharedAssemblyReset?.Invoke(packageSources) == true
            ? activePackages.Select(package => package.PackageId).ToArray()
            : forceReloadPackageIds;
        var candidateIds = effectiveForceReloadPackageIds is null
            ? activePackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : effectiveForceReloadPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotPackageIds = packageSources.Select(snapshot => snapshot.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingSnapshot = activePackages.FirstOrDefault(package => candidateIds.Contains(package.PackageId) && !snapshotPackageIds.Contains(package.PackageId));
        if (missingSnapshot is not null)
        {
            return AppPackagePreflightResult.Failed($"Runtime did not provide a package UI snapshot for '{missingSnapshot.PackageId}'.");
        }

        var candidates = BuildPreflightCandidates(activePackages, packageSources, effectiveForceReloadPackageIds).ToArray();
        if (candidates.Length == 0)
        {
            return AppPackagePreflightResult.Succeeded();
        }

        var sessionFolder = AppPackageSessionDirectories.CreateSessionFolder();
        var ownedDisposables = new List<object>();
        var loadContexts = new List<AppPackageLoadContext>();
        var viewRegistry = new AppPackageViewRegistry();
        var extensionCatalog = new AppPackageExtensionCatalog();
        var backgroundProcessQueue = new BackgroundProcessQueueService();
        var assemblyTracker = new AppPackageAssemblyTracker();
        var sharedAssemblyRegistry = new AppSharedAssemblyRegistry([]);
        var sourceLoader = new AppPackageSourceLoader(
            new AppPackageSourcePreparer(sessionFolder),
            downloadSnapshotAsync ?? ((_, _, _) => throw new InvalidOperationException("Runtime snapshot download is unavailable.")));
        var runtimeWorkStopper = new AppPackageRuntimeWorkStopper(backgroundProcessQueue);
        var unloadCoordinator = new AppPackageUnloadCoordinator(
            viewRegistry,
            extensionCatalog,
            runtimeWorkStopper,
            assemblyTracker,
            sharedAssemblyRegistry,
            ownedDisposable => ownedDisposables.Remove(ownedDisposable),
            loadContext => loadContexts.Remove(loadContext));
        var packageActivator = new AppPackageActivator(
            sharedAssemblyRegistry,
            new AppPackageServiceProviderFactory(extensionCatalog, null, null, null, null, backgroundProcessQueue),
            viewRegistry,
            extensionCatalog,
            isPreflight: true);

        try
        {
            var preparedPackages = new List<AppPreparedPackageActivation>();
            foreach (var (package, source) in candidates)
            {
                var sourceLoadResult = await sourceLoader.LoadAsync(package, source, cancellationToken).ConfigureAwait(false);
                if (!sourceLoadResult.IsSuccess || sourceLoadResult.PreparedSource is null)
                {
                    return AppPackagePreflightResult.Failed(
                        sourceLoadResult.FailureMessage ?? $"Failed to prepare app-side package source for '{package.PackageId}'.");
                }

                preparedPackages.Add(new AppPreparedPackageActivation(package, source, sourceLoadResult.PreparedSource));
            }

            if (preparedPackages.Count > 0)
            {
                sharedAssemblyRegistry.AddProbeDirectories(preparedPackages.Select(package => package.LibraryFolder));
            }

            foreach (var preparedPackage in preparedPackages)
            {
                var result = await PreflightPreparedPackageAsync(
                    preparedPackage,
                    packageActivator,
                    unloadCoordinator,
                    assemblyTracker.RegisterPackageAssembly,
                    loadContexts.Add,
                    ownedDisposables.Add,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    return result;
                }
            }

            return AppPackagePreflightResult.Succeeded();
        }
        finally
        {
            sharedAssemblyRegistry.Dispose();
            AppPackageSourcePreparer.TryDeleteDirectory(sessionFolder);
        }
    }

    private IEnumerable<(ActivePackageDescriptor Package, PackageUiSnapshotDescriptor Source)> BuildPreflightCandidates(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds)
    {
        var deltaPlan = new AppPackageDeltaPlan(activePackages, packageSources, forceReloadPackageIds);
        var candidateIds = forceReloadPackageIds is null
            ? activePackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : forceReloadPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var activePackage in activePackages)
        {
            if (!candidateIds.Contains(activePackage.PackageId))
            {
                continue;
            }

            if (!deltaPlan.TryGetSource(activePackage, out var source))
            {
                continue;
            }

            var action = deltaPlan.GetAction(activePackage, source, getLoadedPackage(activePackage.PackageId), isPackageDisabled(activePackage.PackageId));
            if (action is AppPackageDeltaAction.Load or AppPackageDeltaAction.Reload)
            {
                yield return (activePackage, source);
            }
        }
    }

    private static async Task<AppPackagePreflightResult> PreflightPreparedPackageAsync(
        AppPreparedPackageActivation preparedPackage,
        AppPackageActivator packageActivator,
        AppPackageUnloadCoordinator unloadCoordinator,
        Action<string, Assembly> registerPackageAssembly,
        Action<AppPackageLoadContext> trackLoadContext,
        Action<object> trackOwnedDisposable,
        CancellationToken cancellationToken)
    {
        var activation = new AppPackageActivationState();
        try
        {
            await packageActivator.ActivateAsync(
                preparedPackage.Package,
                preparedPackage.PreparedSource,
                activation,
                registerPackageAssembly,
                trackLoadContext,
                trackOwnedDisposable,
                cancellationToken).ConfigureAwait(false);
            return AppPackagePreflightResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppPackagePreflightResult.Failed($"App-side package preflight failed for '{preparedPackage.Package.PackageId}': {ex.Message}");
        }
        finally
        {
            await unloadCoordinator.RollBackActivationAsync(
                preparedPackage.Package.PackageId,
                activation.PackageInfo,
                activation.ServiceProvider,
                activation.LoadContext,
                stopRuntimeWork: false).ConfigureAwait(false);
        }
    }
}
