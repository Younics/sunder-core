using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageUnloadCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
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
        PackageExtensionOwnerActivation? extensionOwner,
        bool stopRuntimeWork = true)
    {
        var retirement = extensionOwner is null
            ? PackageExtensionOwnerRetirement.Completed(packageId)
            : extensionCatalog.BeginOwnerRetirement(extensionOwner, PackageExtensionCatalogChangeReason.PackageDeactivated);
        if (extensionOwner is not null)
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
                retirement,
                packageInfo?.Folder,
                packageInfo?.LibraryFolder,
                serviceProvider,
                loadContext,
                removeWholePackageTracking: extensionOwner is not null));
    }

    public async Task UnloadPackageAsync(
        string packageId,
        AppLoadedPackageHandle handle,
        bool useRetirementDeadline = true)
    {
        var retirement = handle.ExtensionOwner is null
            ? PackageExtensionOwnerRetirement.Completed(packageId)
            : extensionCatalog.BeginOwnerRetirement(handle.ExtensionOwner, PackageExtensionCatalogChangeReason.PackageDeactivated);
        await viewRegistry.UnregisterPackageAsync(packageId, CancellationToken.None);
        await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);

        removeOwnedDisposable(handle.ServiceProvider);
        removeLoadContext(handle.LoadContext);
        var cleanup = CleanupRetiredPackageAsync(
            packageId,
            retirement,
            handle.Folder,
            Path.Combine(handle.Folder, "lib"),
            handle.ServiceProvider,
            handle.LoadContext,
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

    public async Task RetireAllOwnersAsync()
        => await Task.WhenAll(extensionCatalog
            .BeginAllOwnerRetirements()
            .Select(static retirement => retirement.Completion));

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
        PackageExtensionOwnerRetirement retirement,
        string? packageFolder,
        string? libraryFolder,
        object? serviceProvider,
        AppPackageLoadContext? loadContext,
        bool removeWholePackageTracking)
    {
        await retirement.Completion.ConfigureAwait(false);
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
                $"App package '{packageId}' owner retirement exceeded its {AppShutdownBudgets.GenerationRetirement.TotalSeconds:0.###} second budget; its provider and load context were quarantined without forced disposal.",
                exception);
            AppCleanupQuarantine.Retain(cleanup, $"retiring App package '{packageId}' extension owner");
        }
    }

}
