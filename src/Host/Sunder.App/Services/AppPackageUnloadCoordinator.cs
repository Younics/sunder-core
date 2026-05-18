using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageUnloadCoordinator(
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
    AppPackageRuntimeWorkStopper runtimeWorkStopper,
    AppPackageAssemblyTracker assemblyTracker,
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    Action<object> removeOwnedDisposable,
    Action<AppPackageLoadContext> removeLoadContext)
{
    public async Task RollBackActivationAsync(
        string packageId,
        AppLoadedPackageInfo? packageInfo,
        ServiceProvider? serviceProvider,
        AppPackageLoadContext? loadContext,
        bool stopRuntimeWork = true)
    {
        await viewRegistry.UnregisterPackageAsync(packageId, CancellationToken.None);
        extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageDeactivated);
        if (stopRuntimeWork)
        {
            await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);
        }

        assemblyTracker.RemovePackage(packageId);

        if (packageInfo is not null)
        {
            sharedAssemblyRegistry.RemoveProbeDirectories([packageInfo.LibraryFolder]);
        }

        if (serviceProvider is not null)
        {
            removeOwnedDisposable(serviceProvider);
            await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(serviceProvider, $"Failed to dispose app-side services for package '{packageId}'.");
        }

        if (loadContext is not null)
        {
            removeLoadContext(loadContext);
            AppPackageResourceDisposer.TryUnloadLoadContext(loadContext, packageId);
        }
    }

    public async Task UnloadPackageAsync(string packageId, AppLoadedPackageHandle handle)
    {
        await viewRegistry.UnregisterPackageAsync(packageId, CancellationToken.None);
        extensionCatalog.RemovePackage(packageId, PackageExtensionCatalogChangeReason.PackageDeactivated);
        await runtimeWorkStopper.StopPackageWorkAsync(packageId, CancellationToken.None);

        removeOwnedDisposable(handle.ServiceProvider);
        await Task.Run(
            async () => await AppPackageResourceDisposer.TryDisposeOwnedInstanceAsync(handle.ServiceProvider, $"Failed to dispose app-side services for package '{packageId}'."),
            CancellationToken.None);

        removeLoadContext(handle.LoadContext);
        assemblyTracker.RemovePackage(packageId);
        sharedAssemblyRegistry.RemoveProbeDirectories([Path.Combine(handle.Folder, "lib")]);
        AppPackageResourceDisposer.TryUnloadLoadContext(handle.LoadContext, packageId);
    }

    public async Task DisposeLegacyOwnedInstancesAsync(
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

}
