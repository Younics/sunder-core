using System.Reflection;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageHostComposition : IDisposable
{
    private readonly AppPackageActivator _packageActivator;
    private readonly AppPackageDisableCoordinator _disableCoordinator;
    private readonly object _eventSender;
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry;
    private readonly AppPackageHostState _state;
    private readonly AppPackageUnloadCoordinator _unloadCoordinator;
    private readonly AppPackagePublicationServices _publicationServices;
    private readonly AppPackageGenerationPublication _publication = new();
    private readonly BackgroundProcessQueueService? _ownedBackgroundProcessQueue;

    public AppPackageHostComposition(
        object eventSender,
        Guid generationId,
        AppPackageViewRegistry viewRegistry,
        AppPackageHostState state,
        Action<Guid, string, string, PackageFailureOrigin, Exception?> disablePackage,
        PackageRuntimeFaultReporter? faultReporter,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry,
        AppPackageExtensionCatalog? extensionCatalog,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        AppPackageSessionService? packageSessionService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo)
    {
        _eventSender = eventSender;
        _state = state;
        AssemblyTracker = new AppPackageAssemblyTracker();
        var faultNotificationService = notificationCenter is null
            ? null
            : new AppPackageNotificationService(notificationCenter, "sunder.app", "Sunder");
        FaultNotifier = new AppPackageFaultNotifier(faultNotificationService);
        ViewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            _state.IsPackageDisabled,
            (packageId, message, exception) => disablePackage(
                generationId,
                packageId,
                message,
                PackageFailureOrigin.AppHostedView,
                exception));

        _sharedAssemblyRegistry = sharedAssemblyRegistry ?? new AppSharedAssemblyRegistry([]);
        ExtensionCatalog = extensionCatalog ?? new AppPackageExtensionCatalog();
        var resolvedBackgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
        _ownedBackgroundProcessQueue = backgroundProcessQueue is null ? resolvedBackgroundProcessQueue : null;
        var runtimeWorkStopper = new AppPackageRuntimeWorkStopper(resolvedBackgroundProcessQueue, generationId);
        _publicationServices = new AppPackagePublicationServices(shellViewService, settingsNavigationService, _publication);
        var serviceProviderFactory = new AppPackageServiceProviderFactory(
            ExtensionCatalog,
            _publicationServices,
            _publicationServices,
            packageSessionService,
            notificationCenter,
            resolvedBackgroundProcessQueue,
            _publication,
            generationId);
        _packageActivator = new AppPackageActivator(
            _sharedAssemblyRegistry,
            serviceProviderFactory,
            viewRegistry,
            ExtensionCatalog,
            getRuntimeConnectionInfo: getRuntimeConnectionInfo);
        _unloadCoordinator = new AppPackageUnloadCoordinator(
            viewRegistry,
            ExtensionCatalog,
            runtimeWorkStopper,
            AssemblyTracker,
            _sharedAssemblyRegistry,
            _state.RemoveOwnedDisposable,
            _state.RemoveLoadContext);
        _disableCoordinator = new AppPackageDisableCoordinator(
            viewRegistry,
            ViewFacade,
            ExtensionCatalog,
            runtimeWorkStopper,
            FaultNotifier,
            _state.TryMarkPackageDisabled);
    }

    public AppPackageAssemblyTracker AssemblyTracker { get; }

    public AppPackageFaultNotifier FaultNotifier { get; }

    public AppPackageHostedViewFacade ViewFacade { get; }

    public AppPackageExtensionCatalog ExtensionCatalog { get; }

    public void PublishServices() => _publication.Publish();

    public void UnpublishServices() => _publication.Revoke();

    public void AddSharedAssemblyProbeDirectories(IEnumerable<string> probeDirectories)
        => _sharedAssemblyRegistry.AddProbeDirectories(probeDirectories);

    public async Task ActivatePackageAsync(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        AppPreparedPackageSource preparedSource,
        CancellationToken cancellationToken)
    {
        var activation = new AppPackageActivationState();
        var activated = false;
        try
        {
            await _packageActivator.ActivateAsync(
                package,
                preparedSource,
                activation,
                RegisterPackageAssembly,
                _state.TrackLoadContext,
                _state.TrackOwnedDisposable,
                cancellationToken).ConfigureAwait(false);
            if (activation.PackageInfo is null || activation.ServiceProvider is null || activation.LoadContext is null)
            {
                throw new InvalidOperationException($"Package '{package.PackageId}' activation did not produce a complete app-side package handle.");
            }

            _state.SetLoadedPackage(
                package.PackageId,
                new AppLoadedPackageHandle(
                    package,
                    source,
                    activation.PackageInfo.Folder,
                    activation.ServiceProvider,
                    activation.LoadContext));
            activated = true;
        }
        finally
        {
            if (!activated)
            {
                await _unloadCoordinator.RollBackActivationAsync(
                    package.PackageId,
                    activation.PackageInfo,
                    activation.ServiceProvider,
                    activation.LoadContext).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _ownedBackgroundProcessQueue?.Dispose();
    }

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
            cancellationToken).ConfigureAwait(false);

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

        await _unloadCoordinator.UnloadPackageAsync(packageId, handle).ConfigureAwait(false);
        PackageStateChanged?.Invoke();
        return true;
    }

    public async Task DisposeRemainingOwnedResourcesAsync()
    {
        var (ownedDisposables, loadContexts) = _state.SnapshotOwnedResources();
        await _unloadCoordinator.DisposeOwnedResourcesAsync(ownedDisposables, loadContexts).ConfigureAwait(false);
    }

    public void RegisterPackageAssembly(string packageId, Assembly assembly)
        => AssemblyTracker.RegisterPackageAssembly(packageId, assembly);

    public void DisposeSharedAssemblies()
        => _sharedAssemblyRegistry.Dispose();

    public event Action? PackageStateChanged;

}
