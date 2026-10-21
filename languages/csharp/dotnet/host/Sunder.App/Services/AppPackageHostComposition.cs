using System.Reflection;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppPackageHostComposition : IDisposable
{
    private readonly AppPackageActivator _packageActivator;
    private readonly AppPackageDisableCoordinator _disableCoordinator;
    private readonly object _eventSender;
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry;
    private readonly AppPackageHostState _state;
    private readonly AppPackageUnloadCoordinator _unloadCoordinator;
    private readonly AppPackagePublicationServices _publicationServices;
    private readonly AppPackageGenerationPublication _publication = new();
    private readonly Action<Guid, string, string?, Assembly> _registerFaultAttribution;
    private readonly BackgroundProcessQueueService? _ownedBackgroundProcessQueue;
    private readonly Guid _generationId;
    private readonly Guid _activationOwnerId;
    private int _cleanupUnproven;
    private readonly AppPackageViewRegistry _viewRegistry;
    private readonly AppPackageRuntimeWorkStopper _runtimeWorkStopper;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AppPackageHostComposition> _retained = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retainedActivationIds = new(StringComparer.OrdinalIgnoreCase);

    internal void RetainPackage(string packageId, AppPackageHostComposition owner, bool existingActivation)
    {
        if (!_retained.TryAdd(packageId, owner)) throw new InvalidOperationException("An activation was retained twice.");
        if (existingActivation) _retainedActivationIds.Add(packageId);
        foreach (var registration in owner.AssemblyTracker.SnapshotPackageAssemblies().Where(item => item.PackageId == packageId))
            _registerFaultAttribution(_generationId, packageId, _state.GetPackageContentHash(packageId), registration.Assembly);
    }
    internal void DetachRetainedPackages() { _retained.Clear(); }
    internal bool IsRetainedPackage(string packageId) => _retainedActivationIds.Contains(packageId);
    internal bool ServicesPublished => _publication.IsPublished;
    internal Guid ActivationOwnerId => _activationOwnerId;
    internal bool CanRetainActivations => _publication.IsPublished && CleanupOutcome == AppPackageUiCleanupOutcome.Proven;
    internal IReadOnlySet<string> RetainedPackageIds => _retainedActivationIds;

    public AppPackageHostComposition(
        object eventSender,
        Guid generationId,
        AppPackageViewRegistry viewRegistry,
        AppPackageHostState state,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        AppPackageFaultNoticeCoordinator faultNoticeCoordinator,
        Func<Guid, string, string, string?, Exception, bool> quarantineView,
        Func<Guid, string, Task<AppPackageUiCleanupOutcome>> quarantinePackageBoundariesAsync,
        Action<Guid, string, string?, Assembly> registerFaultAttribution,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        IUiDispatcher uiDispatcher,
        IAppWebViewFactory? webViewFactory = null,
        IAppWebRpcClientFactory? webRpcClientFactory = null,
        ExternalBrowserService? externalBrowser = null,
        IAppPackagePolicy? packagePolicy = null,
        IAppPackageDevelopmentBackend? developerServices = null,
        Guid? activationOwnerId = null)
    {
        _eventSender = eventSender;
        _generationId = generationId;
        _activationOwnerId = activationOwnerId ?? generationId;
        _state = state;
        _viewRegistry = viewRegistry;
        _registerFaultAttribution = registerFaultAttribution;
        AssemblyTracker = new AppPackageAssemblyTracker();
        var packagesById = activePackages
            .GroupBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        FaultNotifier = new AppPackageFaultNotifier(
            faultNoticeCoordinator,
            packageId =>
            {
                var package = packagesById.GetValueOrDefault(packageId);
                return new AppPackageFaultIdentity(
                    packageId,
                    package?.DisplayName ?? packageId,
                    _state.GetPackageContentHash(packageId),
                    generationId);
            },
            uiDispatcher);
        ViewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            _state.IsPackageDisabled,
            (packageId, viewId, exception) => quarantineView(
                generationId,
                packageId,
                viewId,
                _state.GetPackageContentHash(packageId),
                exception));
        ViewFacade.RetainedView = viewId => _viewRegistry.GetPackageId(viewId) is { } packageId
            ? _retained.GetValueOrDefault(packageId)?.ViewFacade : null;
        ViewFacade.RetainedPackage = packageId => _retained.GetValueOrDefault(packageId)?.ViewFacade;
        ViewFacade.RetainedFacades = () => _retained.Values.Distinct().Select(owner => owner.ViewFacade).ToArray();

        _sharedAssemblyRegistry = sharedAssemblyRegistry ?? new AppSharedAssemblyRegistry([]);
        var resolvedBackgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
        _ownedBackgroundProcessQueue = backgroundProcessQueue is null ? resolvedBackgroundProcessQueue : null;
        var runtimeWorkStopper = _runtimeWorkStopper = new AppPackageRuntimeWorkStopper(resolvedBackgroundProcessQueue, activationOwnerId ?? generationId,
            (packageId, token) => _retained.TryGetValue(packageId, out var owner) ? owner._runtimeWorkStopper.StopPackageWorkAsync(packageId, token) : null);
        _publicationServices = new AppPackagePublicationServices(shellViewService, settingsNavigationService, _publication);
        var serviceProviderFactory = new AppPackageServiceProviderFactory(
            _publicationServices,
            _publicationServices,
            notificationCenter,
            resolvedBackgroundProcessQueue,
            _publication,
            activationOwnerId ?? generationId,
            developerServices);
        _packageActivator = new AppPackageActivator(
            _sharedAssemblyRegistry,
            serviceProviderFactory,
            viewRegistry,
            getRuntimeConnectionInfo: getRuntimeConnectionInfo,
            webViewFactory: webViewFactory,
            webRpcClientFactory: webRpcClientFactory,
            externalBrowser: externalBrowser,
            packagePolicy: packagePolicy);
        _unloadCoordinator = new AppPackageUnloadCoordinator(
            viewRegistry,
            runtimeWorkStopper,
            AssemblyTracker,
            _sharedAssemblyRegistry,
            _state.RemoveOwnedDisposable,
            _state.RemoveLoadContext);
        _disableCoordinator = new AppPackageDisableCoordinator(
            viewRegistry,
            ViewFacade,
            runtimeWorkStopper,
            FaultNotifier,
            _state.TryMarkPackageDisabled,
            _state.IsPackageDisabled);
        _quarantinePackageBoundariesAsync = packageId =>
            quarantinePackageBoundariesAsync(_generationId, packageId);
    }

    public AppPackageAssemblyTracker AssemblyTracker { get; }

    public AppPackageFaultNotifier FaultNotifier { get; }

    public AppPackageHostedViewFacade ViewFacade { get; }

    internal AppPackageUiCleanupOutcome CleanupOutcome
        => Volatile.Read(ref _cleanupUnproven) == 0
            ? AppPackageUiCleanupOutcome.Proven
            : AppPackageUiCleanupOutcome.Unproven;

    private readonly Func<string, Task<AppPackageUiCleanupOutcome>>
        _quarantinePackageBoundariesAsync;

    public void PublishServices() => _publication.Publish();

    public void UnpublishServices() => _publication.Revoke();

    internal AppPackageGenerationPublication.DataHandoff BeginDataHandoff() => _publication.BeginDataHandoff();

    public IDisposable BeginRuntimePreparation()
    {
        var scopes = new List<IDisposable>();
        try
        {
            if (!_publication.IsPublished) scopes.Add(_publication.BeginRuntimePreparation());
            foreach (var owner in _retained.Values.Distinct())
                if (!owner.ServicesPublished) scopes.Add(owner.BeginRuntimePreparation());
            return new PreparationScopes(scopes);
        }
        catch { new PreparationScopes(scopes).Dispose(); throw; }
    }

    private sealed class PreparationScopes(List<IDisposable> scopes) : IDisposable
    {
        public void Dispose() { for (var index = scopes.Count - 1; index >= 0; index--) scopes[index].Dispose(); }
    }

    public IReadOnlyList<Task> BeginGenerationRetirement()
    {
        var viewOperations = ViewFacade.BeginCancelAllViewOperations();
        var backgroundOperations = _unloadCoordinator.StopAllOwnedRuntimeWorkAsync();
        return
        [
            viewOperations,
            backgroundOperations,
        ];
    }

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
        var projectionLease = preparedSource.ProjectionLease?.Take();
        try
        {
            await _packageActivator.ActivateAsync(
                package,
                source,
                preparedSource,
                activation,
                _activationOwnerId,
                RegisterPackageAssembly,
                _state.TrackLoadContext,
                _state.TrackOwnedDisposable,
                cancellationToken).ConfigureAwait(false);
            var isWeb = string.Equals(
                source.Target.Kind,
                Sunder.Package.Format.SunderPackageFormat.WebTargetKind,
                StringComparison.Ordinal);
            if (activation.PackageInfo is null
                || isWeb && activation.TargetLifetime is null
                || !isWeb && (activation.ServiceProvider is null || activation.LoadContext is null))
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
                    activation.LoadContext,
                    activation.TargetLifetime,
                    projectionLease) { LoadAdmission = activation.LoadAdmission });
            projectionLease = null;
            activated = true;
        }
        finally
        {
            if (!activated)
            {
                var cleanupOutcome = await _unloadCoordinator.RollBackActivationAsync(
                    package.PackageId,
                    activation.PackageInfo,
                    activation.ServiceProvider,
                    activation.LoadContext,
                    activation.TargetLifetime,
                    projectionLease,
                    loadAdmission: activation.LoadAdmission).ConfigureAwait(false);
                RecordCleanupOutcome(cleanupOutcome);
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
            UnloadPackageForDisableAsync,
            () => _quarantinePackageBoundariesAsync(packageId),
            cancellationToken).ConfigureAwait(false);

    public bool TryBeginDisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception,
        bool deferNotification,
        out Task<AppPackageUiCleanupOutcome> completion)
        => _disableCoordinator.TryBeginDisablePackage(
            _eventSender,
            packageId,
            message,
            origin,
            exception,
            UnloadPackageForDisableAsync,
            () => _quarantinePackageBoundariesAsync(packageId),
            deferNotification,
            out completion);

    public async Task ApplyInheritedPackageQuarantineAsync(string packageId)
    {
        _state.TryMarkPackageDisabled(packageId);
        await _disableCoordinator.CleanupDisabledPackageAsync(
            _eventSender,
            packageId,
            UnloadPackageForDisableAsync,
            () => _quarantinePackageBoundariesAsync(packageId)).ConfigureAwait(false);
    }

    public async Task<bool> UnloadPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default,
        bool preserveDisabled = false)
    {
        if (_retained.TryGetValue(packageId, out var retained))
            return await retained.UnloadPackageAsync(packageId, cancellationToken, preserveDisabled).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await RetireOwnedPackageAsync(packageId, preserveDisabled, useRetirementDeadline: true).ConfigureAwait(false);
        PublishPackageStateChanged();
        return result.WasLoaded;
    }

    private async Task<AppPackageUnloadResult> UnloadPackageForDisableAsync(
        string packageId,
        CancellationToken cancellationToken,
        bool preserveDisabled)
    {
        if (_retained.TryGetValue(packageId, out var retained))
            return await retained.UnloadPackageForDisableAsync(packageId, cancellationToken, preserveDisabled).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await RetireOwnedPackageAsync(packageId, preserveDisabled, useRetirementDeadline: true).ConfigureAwait(false);
        PublishPackageStateChanged();
        return result;
    }

    public Task<AppPackageUnloadResult> UnloadPackageForGenerationRetirementAsync(string packageId)
        => RetireOwnedPackageAsync(packageId, preserveDisabled: true, useRetirementDeadline: false);

    public async Task<AppPackageUiCleanupOutcome> DisposeRemainingOwnedResourcesAsync()
    {
        var (ownedDisposables, loadContexts) = _state.SnapshotOwnedResources();
        var cleanupOutcome = await _unloadCoordinator.DisposeOwnedResourcesAsync(
            ownedDisposables,
            loadContexts).ConfigureAwait(false);
        RecordCleanupOutcome(cleanupOutcome);
        return cleanupOutcome;
    }

    public void RegisterPackageAssembly(string packageId, Assembly assembly)
    {
        AssemblyTracker.RegisterPackageAssembly(packageId, assembly);
        _registerFaultAttribution(
            _generationId,
            packageId,
            _state.GetPackageContentHash(packageId),
            assembly);
    }

    public void DisposeSharedAssemblies()
        => _sharedAssemblyRegistry.Dispose();

    public event Action? PackageStateChanged;

    private void PublishPackageStateChanged()
    {
        var handlers = PackageStateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception) when (
                AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                AppSessionLog.WriteError(
                    "An App package state subscriber failed during package cleanup.",
                    exception);
            }
        }
    }

    internal void RecordCleanupOutcome(AppPackageUiCleanupOutcome cleanupOutcome)
    {
        if (cleanupOutcome == AppPackageUiCleanupOutcome.Unproven)
        {
            Interlocked.Exchange(ref _cleanupUnproven, 1);
        }
    }

}
