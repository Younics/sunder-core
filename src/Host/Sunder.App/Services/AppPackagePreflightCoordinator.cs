using System.Reflection;
using Sunder.Protocol;

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
    Func<string, bool> isPackageDisabled)
{
    public async Task<AppPackagePreflightResult> PreflightPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = BuildPreflightCandidates(activePackages, packageSources, forceReloadPackageIds).ToArray();
        if (candidates.Length == 0)
        {
            return AppPackagePreflightResult.Succeeded();
        }

        var sessionFolder = AppPackageSessionDirectories.CreateSessionFolder();
        var ownedDisposables = new List<object>();
        var loadContexts = new List<AppPackageLoadContext>();
        var viewRegistry = new AppPackageViewRegistry();
        var backgroundServices = new AppPackageBackgroundServiceCoordinator();
        var extensionCatalog = new AppPackageExtensionCatalog();
        var backgroundProcessQueue = new BackgroundProcessQueueService();
        var assemblyTracker = new AppPackageAssemblyTracker();
        var sharedAssemblyRegistry = new AppSharedAssemblyRegistry([]);
        var sourceLoader = new AppPackageSourceLoader(new AppPackageSourcePreparer(sessionFolder));
        var runtimeWorkStopper = new AppPackageRuntimeWorkStopper(backgroundServices, backgroundProcessQueue);
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
            backgroundServices,
            extensionCatalog);

        try
        {
            foreach (var (package, source) in candidates)
            {
                var result = await PreflightPackageAsync(
                    package,
                    source,
                    sourceLoader,
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

    private IEnumerable<(ActivePackageDescriptor Package, PackageSourceDescriptor Source)> BuildPreflightCandidates(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
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
                yield return (activePackage, new PackageSourceDescriptor(activePackage.PackageId, PackageSourceKind.Installed, string.Empty));
                continue;
            }

            var action = deltaPlan.GetAction(activePackage, source, getLoadedPackage(activePackage.PackageId), isPackageDisabled(activePackage.PackageId));
            if (action is AppPackageDeltaAction.Load or AppPackageDeltaAction.Reload)
            {
                yield return (activePackage, source);
            }
        }
    }

    private static async Task<AppPackagePreflightResult> PreflightPackageAsync(
        ActivePackageDescriptor package,
        PackageSourceDescriptor source,
        AppPackageSourceLoader sourceLoader,
        AppPackageActivator packageActivator,
        AppPackageUnloadCoordinator unloadCoordinator,
        Action<string, Assembly> registerPackageAssembly,
        Action<AppPackageLoadContext> trackLoadContext,
        Action<object> trackOwnedDisposable,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Folder))
        {
            return AppPackagePreflightResult.Failed($"Runtime did not provide a loadable app-side package source for '{package.PackageId}'.");
        }

        var sourceLoadResult = await sourceLoader.LoadAsync(package, source, cancellationToken).ConfigureAwait(false);
        if (!sourceLoadResult.IsSuccess || sourceLoadResult.PreparedSource is null)
        {
            return AppPackagePreflightResult.Failed(
                sourceLoadResult.FailureMessage ?? $"Failed to prepare app-side package source for '{package.PackageId}'.");
        }

        var activation = new AppPackageActivationState();
        try
        {
            await packageActivator.ActivateAsync(
                package,
                sourceLoadResult.PreparedSource,
                activation,
                registerPackageAssembly,
                trackLoadContext,
                trackOwnedDisposable,
                cancellationToken,
                startBackgroundServices: false).ConfigureAwait(false);
            return AppPackagePreflightResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppPackagePreflightResult.Failed($"App-side package preflight failed for '{package.PackageId}': {ex.Message}");
        }
        finally
        {
            await unloadCoordinator.RollBackActivationAsync(
                package.PackageId,
                activation.PackageInfo,
                activation.ServiceProvider,
                activation.LoadContext,
                stopRuntimeWork: false).ConfigureAwait(false);
        }
    }
}
