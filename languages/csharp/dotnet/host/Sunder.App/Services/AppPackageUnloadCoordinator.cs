using Microsoft.Extensions.DependencyInjection;

namespace Sunder.App.Services;

internal sealed class AppPackageUnloadCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageRuntimeWorkStopper runtimeWorkStopper,
    AppPackageAssemblyTracker assemblyTracker,
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    Action<object> removeOwnedDisposable,
    Action<AppPackageLoadContext> removeLoadContext,
    Action<string>? removePackageResourceAssemblies = null)
{
    public async Task RollBackActivationAsync(
        string packageId,
        AppLoadedPackageInfo? packageInfo,
        ServiceProvider? serviceProvider,
        AppPackageLoadContext? loadContext,
        IAsyncDisposable? targetLifetime = null,
        bool stopRuntimeWork = true)
    {
        if (packageInfo is not null || targetLifetime is not null)
        {
            await viewRegistry.UnregisterPackageAsync(packageId, CancellationToken.None);
            if (stopRuntimeWork)
            {
                await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);
            }
        }

        if (serviceProvider is not null)
        {
            removeOwnedDisposable(serviceProvider);
        }
        if (loadContext is not null)
        {
            removeLoadContext(loadContext);
        }

        await RunBoundedOwnerCleanupAsync(
            packageId,
            CleanupRetiredPackageAsync(
                packageId,
                packageInfo?.Folder,
                packageInfo?.LibraryFolder,
                serviceProvider,
                loadContext,
                targetLifetime,
                removeWholePackageTracking: packageInfo is not null || targetLifetime is not null));
    }

    public async Task UnloadPackageAsync(
        string packageId,
        AppLoadedPackageHandle handle,
        bool useRetirementDeadline = true)
    {
        await viewRegistry.UnregisterPackageAsync(packageId, CancellationToken.None);
        await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);

        if (handle.ServiceProvider is not null)
        {
            removeOwnedDisposable(handle.ServiceProvider);
        }
        if (handle.LoadContext is not null)
        {
            removeLoadContext(handle.LoadContext);
        }
        var cleanup = CleanupRetiredPackageAsync(
            packageId,
            handle.Folder,
            Path.Combine(handle.Folder, "lib"),
            handle.ServiceProvider,
            handle.LoadContext,
            handle.TargetLifetime,
            removeWholePackageTracking: true);
        if (useRetirementDeadline)
        {
            await RunBoundedOwnerCleanupAsync(packageId, cleanup);
        }
        else
        {
            await cleanup;
        }
    }

    public Task StopAllOwnedRuntimeWorkAsync()
        => runtimeWorkStopper.StopAllOwnedWorkAsync(CancellationToken.None);

    public async Task DisposeOwnedResourcesAsync(
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts)
    {
        foreach (var disposable in ownedDisposables)
        {
            await Task.Run(
                async () => await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(disposable, "Failed to dispose an app-side package service container."));
        }

        foreach (var loadContext in loadContexts)
        {
            AppPackageResourceDisposer.TryUnloadLoadContext(loadContext, packageId: null);
        }
    }

    private async Task CleanupRetiredPackageAsync(
        string packageId,
        string? packageFolder,
        string? libraryFolder,
        object? serviceProvider,
        AppPackageLoadContext? loadContext,
        IAsyncDisposable? targetLifetime,
        bool removeWholePackageTracking)
    {
        if (removeWholePackageTracking)
        {
            assemblyTracker.RemovePackage(packageId);
            removePackageResourceAssemblies?.Invoke(packageId);
        }
        else if (loadContext is not null)
        {
            assemblyTracker.RemoveLoadContext(loadContext);
        }
        var canDeletePackageFolder = libraryFolder is null
            || sharedAssemblyRegistry.TryRemoveProbeDirectories([libraryFolder]);

        if (serviceProvider is not null)
        {
            await Task.Run(
                async () => await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(
                    serviceProvider,
                    $"Failed to dispose app-side services for package '{packageId}'."),
                CancellationToken.None);
        }

        if (targetLifetime is not null)
        {
            try
            {
                await targetLifetime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError(
                    $"Failed to dispose app-side target resources for package '{packageId}'.",
                    exception);
            }
        }

        if (loadContext is not null)
        {
            AppPackageResourceDisposer.TryUnloadLoadContext(loadContext, packageId);
        }
        if (packageFolder is not null && canDeletePackageFolder)
        {
            AppPackageSourcePreparer.TryDeleteDirectory(packageFolder);
        }
    }

    private static async Task RunBoundedOwnerCleanupAsync(string packageId, Task cleanup)
    {
        try
        {
            await cleanup.WaitAsync(AppShutdownBudgets.GenerationRetirement).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            AppSessionLog.WriteError(
                $"App package '{packageId}' retirement exceeded its {AppShutdownBudgets.GenerationRetirement.TotalSeconds:0.###} second budget; its provider and load context were quarantined without forced disposal.",
                exception);
            AppCleanupQuarantine.Retain(cleanup, $"retiring App package '{packageId}'");
        }
    }

}
