using System.Reflection;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageHostComposition
{
    private readonly AppPackageDeltaCoordinator _deltaCoordinator;
    private readonly AppPackageDisableCoordinator _disableCoordinator;
    private readonly object _eventSender;
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry;
    private readonly AppPackageHostState _state;
    private readonly AppPackageUnloadCoordinator _unloadCoordinator;

    public AppPackageHostComposition(
        object eventSender,
        AppPackageViewRegistry viewRegistry,
        AppPackageBackgroundServiceCoordinator backgroundServices,
        AppPackageHostState state,
        PackageRuntimeFaultReporter? faultReporter,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry,
        AppPackageExtensionCatalog? extensionCatalog,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue)
    {
        _eventSender = eventSender;
        _state = state;
        AssemblyTracker = new AppPackageAssemblyTracker();
        FaultNotifier = new AppPackageFaultNotifier(faultReporter);
        ViewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            _state.IsPackageDisabled,
            (packageId, message, exception) => DisablePackage(packageId, message, PackageFailureOrigin.AppHostedView, exception));

        var sourceLoader = new AppPackageSourceLoader(new AppPackageSourcePreparer(sessionFolder));
        var resolvedSharedAssemblyRegistry = sharedAssemblyRegistry ?? new AppSharedAssemblyRegistry([]);
        _sharedAssemblyRegistry = resolvedSharedAssemblyRegistry;
        var resolvedExtensionCatalog = extensionCatalog ?? new AppPackageExtensionCatalog();
        var resolvedBackgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
        var runtimeWorkStopper = new AppPackageRuntimeWorkStopper(backgroundServices, resolvedBackgroundProcessQueue);
        var serviceProviderFactory = new AppPackageServiceProviderFactory(
            resolvedExtensionCatalog,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            resolvedBackgroundProcessQueue);
        var packageActivator = new AppPackageActivator(
            resolvedSharedAssemblyRegistry,
            serviceProviderFactory,
            viewRegistry,
            backgroundServices,
            resolvedExtensionCatalog);

        _unloadCoordinator = new AppPackageUnloadCoordinator(
            viewRegistry,
            resolvedExtensionCatalog,
            runtimeWorkStopper,
            AssemblyTracker,
            resolvedSharedAssemblyRegistry,
            _state.RemoveOwnedDisposable,
            _state.RemoveLoadContext);
        _disableCoordinator = new AppPackageDisableCoordinator(
            viewRegistry,
            resolvedExtensionCatalog,
            runtimeWorkStopper,
            FaultNotifier,
            _state.TryMarkPackageDisabled);
        var loadCoordinator = new AppPackageLoadCoordinator(
            sourceLoader,
            packageActivator,
            _unloadCoordinator,
            DisablePackageAsync,
            RegisterPackageAssembly,
            _state.TrackLoadContext,
            _state.TrackOwnedDisposable,
            _state.SetLoadedPackage);
        _deltaCoordinator = new AppPackageDeltaCoordinator(
            _state.SnapshotLoadedPackageIds,
            _state.GetLoadedPackage,
            _state.IsPackageDisabled,
            UnloadPackageAsync,
            loadCoordinator.LoadPackageAsync,
            DisablePackageAsync);
    }

    public AppPackageAssemblyTracker AssemblyTracker { get; }

    public AppPackageFaultNotifier FaultNotifier { get; }

    public AppPackageHostedViewFacade ViewFacade { get; }

    public Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
        => _deltaCoordinator.ApplyPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);

    public void DisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null)
        => _ = DisablePackageAndLogAsync(packageId, message, origin, exception);

    public async Task DisablePackageAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => await _disableCoordinator.DisablePackageAsync(
            _eventSender,
            packageId,
            message,
            origin,
            exception,
            UnloadPackageAsync,
            cancellationToken);

    public async Task<bool> UnloadPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default,
        bool preserveDisabled = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_state.TryRemoveLoadedPackage(packageId, preserveDisabled, out var handle) || handle is null)
        {
            return false;
        }

        await _unloadCoordinator.UnloadPackageAsync(packageId, handle);
        return true;
    }

    public async Task DisposeLegacyOwnedInstancesAsync()
    {
        var (ownedDisposables, loadContexts) = _state.SnapshotLegacyResources();
        await _unloadCoordinator.DisposeLegacyOwnedInstancesAsync(ownedDisposables, loadContexts);
    }

    public void RegisterPackageAssembly(string packageId, Assembly assembly)
        => AssemblyTracker.RegisterPackageAssembly(packageId, assembly);

    public void DisposeSharedAssemblies()
        => _sharedAssemblyRegistry.Dispose();

    private async Task DisablePackageAndLogAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        try
        {
            await DisablePackageAsync(packageId, message, origin, exception);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed to complete package disable for '{packageId}'.", ex);
        }
    }
}
