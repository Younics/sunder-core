using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Projections;

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
    public async Task<AppPackageUiCleanupOutcome> RollBackActivationAsync(
        string packageId,
        AppLoadedPackageInfo? packageInfo,
        ServiceProvider? serviceProvider,
        AppPackageLoadContext? loadContext,
        IAsyncDisposable? targetLifetime = null,
        PackageProjectionAcquisition? projectionLease = null,
        bool stopRuntimeWork = true,
        AppPackageLoadAdmission? loadAdmission = null)
    {
        using var ownership = loadAdmission?.EnterCleanup();
        using var cleanupAuthority = (targetLifetime as AppPackageManagedRpcClient)?.BeginCleanup();
        var cleanupProven = true;
        if (packageInfo is not null || targetLifetime is not null)
        {
            var viewCleanup = await viewRegistry.UnregisterPackageWithOutcomeAsync(
                packageId,
                CancellationToken.None);
            cleanupProven &= viewCleanup.CleanupProven;
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

        cleanupProven &= await RunBoundedOwnerCleanupAsync(
            packageId,
            CleanupRetiredPackageAsync(
                packageId,
                packageInfo?.Folder,
                packageInfo?.LibraryFolder,
                serviceProvider,
                loadContext,
                targetLifetime,
                projectionLease,
                removeWholePackageTracking: packageInfo is not null || targetLifetime is not null,
                loadAdmission,
                cleanupProven,
                retireActivation: true));
        return ToCleanupOutcome(cleanupProven);
    }

    public Task<AppPackageUiCleanupOutcome> UnloadPackageAsync(
        string packageId,
        AppLoadedPackageHandle handle,
        bool useRetirementDeadline = true)
        => UnloadPackageCoreAsync(packageId, handle, useRetirementDeadline, retireActivation: true);

    // Composition owns the memoized, actual local drain and retries its separate
    // Host acknowledgement. A caller timeout must not complete this drain early.
    internal Task<AppPackageUiCleanupOutcome> DrainPackageAsync(string packageId, AppLoadedPackageHandle handle)
        => UnloadPackageCoreAsync(packageId, handle, useRetirementDeadline: false, retireActivation: false);

    private async Task<AppPackageUiCleanupOutcome> UnloadPackageCoreAsync(
        string packageId,
        AppLoadedPackageHandle handle,
        bool useRetirementDeadline,
        bool retireActivation)
    {
        using var ownership = handle.LoadAdmission?.EnterCleanup();
        using var cleanupAuthority = (handle.TargetLifetime as AppPackageManagedRpcClient)?.BeginCleanup();
        var viewCleanup = await viewRegistry.UnregisterPackageWithOutcomeAsync(
            packageId,
            CancellationToken.None);
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
            handle.ProjectionLease,
            removeWholePackageTracking: true,
            handle.LoadAdmission,
            viewCleanup.CleanupProven,
            retireActivation);
        if (useRetirementDeadline)
        {
            var ownerCleanup = await RunBoundedOwnerCleanupAsync(packageId, cleanup).ConfigureAwait(false);
            return ToCleanupOutcome(viewCleanup.CleanupProven && ownerCleanup);
        }

        var drained = await cleanup.ConfigureAwait(false);
        return ToCleanupOutcome(viewCleanup.CleanupProven && drained);
    }

    public Task StopAllOwnedRuntimeWorkAsync()
        => runtimeWorkStopper.StopAllOwnedWorkAsync(CancellationToken.None);

    public async Task<AppPackageUiCleanupOutcome> DisposeOwnedResourcesAsync(
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts)
    {
        var cleanupProven = true;
        foreach (var disposable in ownedDisposables)
        {
            var disposed = await Task.Run(
                async () => await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(
                    disposable,
                    "Failed to dispose an app-side package service container."));
            cleanupProven &= disposed;
            if (!disposed)
            {
                AppCleanupQuarantine.Retain(
                    disposable,
                    "disposing an app-side package service container");
            }
        }

        foreach (var loadContext in loadContexts)
        {
            var unloaded = AppPackageResourceDisposer.TryUnloadLoadContext(
                loadContext,
                packageId: null);
            cleanupProven &= unloaded;
            if (!unloaded)
            {
                AppCleanupQuarantine.Retain(
                    loadContext,
                    "unloading an app-side package load context");
            }
        }
        return ToCleanupOutcome(cleanupProven);
    }

    private async Task<bool> CleanupRetiredPackageAsync(
        string packageId,
        string? packageFolder,
        string? libraryFolder,
        object? serviceProvider,
        AppPackageLoadContext? loadContext,
        IAsyncDisposable? targetLifetime,
        PackageProjectionAcquisition? projectionLease,
        bool removeWholePackageTracking,
        AppPackageLoadAdmission? loadAdmission,
        bool cleanupProven,
        bool retireActivation)
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
        var canDeletePackageFolder = projectionLease is null
            && (libraryFolder is null
                || sharedAssemblyRegistry.TryRemoveProbeDirectories([libraryFolder]));
        if (projectionLease is not null && libraryFolder is not null)
        {
            _ = sharedAssemblyRegistry.TryRemoveProbeDirectories([libraryFolder]);
        }

        if (serviceProvider is not null)
        {
            var disposed = await Task.Run(
                async () => await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(
                    serviceProvider,
                    $"Failed to dispose app-side services for package '{packageId}'."),
                CancellationToken.None);
            cleanupProven &= disposed;
            if (!disposed)
            {
                AppCleanupQuarantine.Retain(
                    serviceProvider,
                    $"disposing app-side services for package '{packageId}'");
            }
        }

        if (targetLifetime is not null)
        {
            try
            {
                await targetLifetime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                cleanupProven = false;
                AppCleanupQuarantine.Retain(
                    targetLifetime,
                    $"disposing app-side target resources for package '{packageId}'");
                AppSessionLog.WriteError(
                    $"Failed to dispose app-side target resources for package '{packageId}'.",
                    exception);
            }
        }

        if (loadContext is not null)
        {
            var unloaded = AppPackageResourceDisposer.TryUnloadLoadContext(
                loadContext,
                packageId);
            cleanupProven &= unloaded;
            if (!unloaded)
            {
                AppCleanupQuarantine.Retain(
                    loadContext,
                    $"unloading app-side package load context for '{packageId}'");
            }
        }
        if (projectionLease is not null)
        {
            if (cleanupProven)
            {
                try
                {
                    await projectionLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
                {
                    cleanupProven = false;
                    AppCleanupQuarantine.Retain(
                        projectionLease,
                        $"releasing immutable package projection for '{packageId}'");
                    AppSessionLog.WriteError(
                        $"Failed to release immutable package projection for '{packageId}'.",
                        exception);
                }
            }
            else
            {
                AppCleanupQuarantine.Retain(
                    projectionLease,
                    $"retaining immutable package projection after unproven cleanup for '{packageId}'");
            }
        }
        if (cleanupProven && packageFolder is not null && canDeletePackageFolder)
        {
            AppPackageSourcePreparer.TryDeleteDirectory(packageFolder);
        }
        // Closing a transport is not activation retirement. Only a proven local
        // drain may acknowledge active use; loaded security remains process-owned.
        if (cleanupProven && retireActivation && loadAdmission is not null)
        {
            try { await loadAdmission.RetireAsync().ConfigureAwait(false); }
            catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                cleanupProven = false;
                AppCleanupQuarantine.Retain(loadAdmission, $"retiring App activation policy for '{packageId}'");
                AppSessionLog.WriteError($"The Host did not acknowledge retirement of App activation '{packageId}'.", exception);
            }
        }
        return cleanupProven;
    }

    private static async Task<bool> RunBoundedOwnerCleanupAsync(
        string packageId,
        Task<bool> cleanup)
    {
        try
        {
            return await cleanup.WaitAsync(AppShutdownBudgets.GenerationRetirement)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            AppSessionLog.WriteError(
                $"App package '{packageId}' retirement exceeded its {AppShutdownBudgets.GenerationRetirement.TotalSeconds:0.###} second budget; its provider and load context were quarantined without forced disposal.",
                exception);
            AppCleanupQuarantine.Retain(cleanup, $"retiring App package '{packageId}'");
            return false;
        }
    }

    private static AppPackageUiCleanupOutcome ToCleanupOutcome(bool cleanupProven)
        => cleanupProven
            ? AppPackageUiCleanupOutcome.Proven
            : AppPackageUiCleanupOutcome.Unproven;

}
