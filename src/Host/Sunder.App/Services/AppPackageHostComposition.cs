using System.Reflection;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageHostComposition : IDisposable
{
    private readonly OwnedTaskObserver _tasks = new(nameof(AppPackageHostComposition));
    private readonly AppPackageDeltaCoordinator _deltaCoordinator;
    private readonly AppPackageDisableCoordinator _disableCoordinator;
    private readonly object _eventSender;
    private readonly AppPackagePreflightCoordinator _preflightCoordinator;
    private readonly AppPackageResourceAssemblyRegistry? _resourceAssemblyRegistry;
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry;
    private readonly AppPackageHostState _state;
    private readonly AppPackageUnloadCoordinator _unloadCoordinator;

    public AppPackageHostComposition(
        object eventSender,
        AppPackageViewRegistry viewRegistry,
        AppPackageHostState state,
        PackageRuntimeFaultReporter? faultReporter,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry,
        AppPackageExtensionCatalog? extensionCatalog,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        AppPackageSessionService? packageSessionService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task>? downloadPackageUiSnapshotAsync = null)
    {
        _eventSender = eventSender;
        _resourceAssemblyRegistry = resourceAssemblyRegistry;
        _state = state;
        AssemblyTracker = new AppPackageAssemblyTracker();
        var faultNotificationService = notificationCenter is null
            ? null
            : new AppPackageNotificationService(notificationCenter, "sunder.app", "Sunder");
        FaultNotifier = new AppPackageFaultNotifier(faultReporter, faultNotificationService);
        ViewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            _state.IsPackageDisabled,
            (packageId, message, exception) => DisablePackage(packageId, message, PackageFailureOrigin.AppHostedView, exception));

        async Task DownloadSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken)
        {
            if (downloadPackageUiSnapshotAsync is not null)
            {
                await downloadPackageUiSnapshotAsync(snapshot, destination, cancellationToken);
                return;
            }

            if (getRuntimeConnectionInfo is null)
            {
                throw new InvalidOperationException("Runtime connection information is required to download package UI snapshots.");
            }

            using var client = new RuntimeApiClient(getRuntimeConnectionInfo);
            await client.DownloadPackageUiSnapshotAsync(snapshot, destination, cancellationToken);
        }

        var sourceLoader = new AppPackageSourceLoader(new AppPackageSourcePreparer(sessionFolder), DownloadSnapshotAsync);
        var resolvedSharedAssemblyRegistry = sharedAssemblyRegistry ?? new AppSharedAssemblyRegistry([]);
        _sharedAssemblyRegistry = resolvedSharedAssemblyRegistry;
        var resolvedExtensionCatalog = extensionCatalog ?? new AppPackageExtensionCatalog();
        ExtensionCatalog = resolvedExtensionCatalog;
        var resolvedBackgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
        var runtimeWorkStopper = new AppPackageRuntimeWorkStopper(resolvedBackgroundProcessQueue);
        var serviceProviderFactory = new AppPackageServiceProviderFactory(
            resolvedExtensionCatalog,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            resolvedBackgroundProcessQueue);
        var packageActivator = new AppPackageActivator(
            resolvedSharedAssemblyRegistry,
            serviceProviderFactory,
            viewRegistry,
            resolvedExtensionCatalog,
            getRuntimeConnectionInfo: getRuntimeConnectionInfo);

        _unloadCoordinator = new AppPackageUnloadCoordinator(
            viewRegistry,
            resolvedExtensionCatalog,
            runtimeWorkStopper,
            AssemblyTracker,
            resolvedSharedAssemblyRegistry,
            _state.RemoveOwnedDisposable,
            _state.RemoveLoadContext,
            RemovePackageResourceAssemblies);
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
            DisablePackageAsync,
            RequiresSharedAssemblyReset,
            resolvedSharedAssemblyRegistry.ResetPackageAssemblies,
            loadCoordinator.PreparePackageAsync,
            loadCoordinator.ActivatePreparedPackageAsync,
            resolvedSharedAssemblyRegistry.AddProbeDirectories);
        _preflightCoordinator = new AppPackagePreflightCoordinator(
            _state.GetLoadedPackage,
            _state.IsPackageDisabled,
            RequiresSharedAssemblyReset,
            DownloadSnapshotAsync);

        bool RequiresSharedAssemblyReset(IReadOnlyList<PackageUiSnapshotDescriptor> packageSources)
        {
            return packageSources.Any(snapshot =>
                _state.GetLoadedPackage(snapshot.PackageId) is { } loaded
                && !string.Equals(loaded.Source.ContentHash, snapshot.ContentHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    public AppPackageAssemblyTracker AssemblyTracker { get; }

    public AppPackageFaultNotifier FaultNotifier { get; }

    public AppPackageHostedViewFacade ViewFacade { get; }

    public AppPackageExtensionCatalog ExtensionCatalog { get; }

    public Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
        => _deltaCoordinator.ApplyPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);

    public Task<AppPackagePreflightResult> PreflightPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds,
        CancellationToken cancellationToken)
        => _preflightCoordinator.PreflightPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);

    public void DisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null)
        => _tasks.Observe(
            DisablePackageAndLogAsync(packageId, message, origin, exception),
            $"disabling package '{packageId}'");

    public void Dispose() => _tasks.Dispose();

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

    public async Task DisposeRemainingOwnedResourcesAsync()
    {
        var (ownedDisposables, loadContexts) = _state.SnapshotOwnedResources();
        await _unloadCoordinator.DisposeOwnedResourcesAsync(ownedDisposables, loadContexts);
    }

    public void RegisterPackageAssembly(string packageId, Assembly assembly)
    {
        AssemblyTracker.RegisterPackageAssembly(packageId, assembly);
        if (_resourceAssemblyRegistry is not null)
        {
            AppPackageAvaloniaAssetLoader.TryInvalidateAssemblyCache(_resourceAssemblyRegistry.RegisterPackageAssembly(packageId, assembly));
        }
    }

    public void DisposeSharedAssemblies()
        => _sharedAssemblyRegistry.Dispose();

    private void RemovePackageResourceAssemblies(string packageId)
    {
        if (_resourceAssemblyRegistry is null)
        {
            return;
        }

        AppPackageAvaloniaAssetLoader.TryInvalidateAssemblyCache(_resourceAssemblyRegistry.RemovePackage(packageId));
    }

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
