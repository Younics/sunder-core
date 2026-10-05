using Avalonia.Controls;
using Sunder.App.Views.Controls;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class PackageViewHostFaultEventArgs(
    string packageId,
    string message,
    PackageFailureOrigin origin,
    string? viewId = null,
    string? correlationId = null) : EventArgs
{
    public string PackageId { get; } = packageId;

    public string Message { get; } = message;

    public PackageFailureOrigin Origin { get; } = origin;

    public string? ViewId { get; } = viewId;

    public string? CorrelationId { get; } = correlationId;
}

public sealed record PackageSettingsViewDescriptor(
    string PackageId,
    string DisplayName,
    string? Summary);

public sealed class PackageViewHostService : IAsyncDisposable
{
    public static PackageViewHostService Empty { get; } = new(
        new AppPackageViewRegistry(),
        [],
        [],
        [],
        sessionFolder: null,
        backgroundProcessQueue: null);

    private readonly AppPackageLifecycleGate _lifecycleGate = new(nameof(PackageViewHostService));
    private readonly OwnedTaskObserver _faultTasks;
    private readonly AppPackageUiFaultRouter _faultRouter = new();
    private readonly AppPackageFaultAttributionIndex _faultAttribution = new();
    private readonly AppPackageFaultNoticeCoordinator _faultNoticeCoordinator;
    private readonly object _boundarySyncRoot = new();
    private readonly Dictionary<Guid, List<WeakReference<HostedPackageViewBoundary>>> _boundariesByGeneration = [];
    private readonly AppPackageQuarantineCoordinator _quarantineCoordinator;
    private readonly Action<Exception> _propagateFatalCleanupFault;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly AppPackageSnapshotCache _snapshotCache;
    private readonly AppPackageGenerationBuilder _generationBuilder;
    private readonly AppPackageGenerationPublisher _generationPublisher;
    private readonly PackageIconGenerationCoordinator _iconCoordinator;
    private readonly AppPackageGenerationRetirementQueue _retirementOwner;
    private readonly AppPackageLifecycleCoordinator _lifecycleCoordinator;
    private string? _sessionFolder;
    private event EventHandler<PackageViewHostFaultEventArgs>? PackageFaultedHandlers;
    private event Action<AppPackageUiRecoveryRequest>? PackageUiRecoveryRequestedHandlers;

    internal PackageViewHostService(
        AppPackageViewRegistry viewRegistry,
        HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry = null,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo = null,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task>? downloadPackageUiSnapshotAsync = null,
        Func<string, CancellationToken, Task<PackageIconImageLoadResult>>? loadPackageIconImageAsync = null,
        IUiDispatcher? uiDispatcher = null,
        Func<AppPackageGeneration, ValueTask>? retireGenerationAsync = null,
        TimeSpan? retirementBudget = null,
        TimeSpan? retirementDrainBudget = null,
        string? packageContentCacheRoot = null,
        Action<Exception>? fatalCleanupFaultHandler = null,
        IAppPackagePolicy? packagePolicy = null)
    {
        _sessionFolder = sessionFolder;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        viewRegistry.SetUiDispatcher(_uiDispatcher);
        _propagateFatalCleanupFault = fatalCleanupFaultHandler
            ?? AppPackageFatalCleanupPropagation.PostToDispatcher;
        _faultTasks = new OwnedTaskObserver(
            nameof(PackageViewHostService),
            _propagateFatalCleanupFault);
        var faultNotificationService = notificationCenter is null
            ? null
            : new AppPackageNotificationService(notificationCenter, "sunder.app", "Sunder");
        _faultNoticeCoordinator = new AppPackageFaultNoticeCoordinator(
            faultNotificationService,
            _propagateFatalCleanupFault);
        _quarantineCoordinator = new AppPackageQuarantineCoordinator(
            () => CurrentGeneration);

        async Task DownloadSnapshotAsync(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken)
        {
            if (downloadPackageUiSnapshotAsync is not null)
            {
                await downloadPackageUiSnapshotAsync(snapshot, destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (getRuntimeConnectionInfo is null)
            {
                throw new InvalidOperationException("Runtime connection information is required to download package UI snapshots.");
            }

            using var client = new RuntimeApiClient(getRuntimeConnectionInfo);
            await client.DownloadPackageUiSnapshotAsync(snapshot, destination, cancellationToken).ConfigureAwait(false);
        }

        var resolvedContentCacheRoot = packageContentCacheRoot
            ?? (downloadPackageUiSnapshotAsync is null
                ? AppLocalState.GetPath("package-content-cache")
                : Path.Combine(EnsureSessionFolder(), "package-content-cache"));
        _snapshotCache = new AppPackageSnapshotCache(EnsureSessionFolder, DownloadSnapshotAsync, resolvedContentCacheRoot);
        async ValueTask RetireGenerationAndAttributionsAsync(AppPackageGeneration generation)
        {
            var registrations = generation.SnapshotPackageAssemblies();
            if (retireGenerationAsync is null)
            {
                await generation.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await retireGenerationAsync(generation).ConfigureAwait(false);
            }

            _quarantineCoordinator.UnregisterPendingCandidate(generation);
            _faultAttribution.Unregister(generation.Id, registrations);
        }

        _retirementOwner = new AppPackageGenerationRetirementQueue(
            RetireGenerationAndAttributionsAsync,
            retirementBudget,
            retirementDrainBudget,
            _propagateFatalCleanupFault);
        _generationBuilder = new AppPackageGenerationBuilder(
            this,
            _snapshotCache,
            EnsureSessionFolder,
            TryRouteKnownViewFailureForGeneration,
            QuarantinePackageBoundariesAsync,
            RegisterPackageFaultAttribution,
            _quarantineCoordinator.RegisterPendingCandidate,
            shellViewService,
            settingsNavigationService,
            _faultNoticeCoordinator,
            notificationCenter,
            backgroundProcessQueue,
            getRuntimeConnectionInfo,
            _uiDispatcher,
            packagePolicy);
        var initialGeneration = _generationBuilder.CreateInitialGeneration(
            viewRegistry,
            disabledPackageIds,
            ownedDisposables,
            loadContexts,
            sharedAssemblyRegistry);
        _iconCoordinator = new PackageIconGenerationCoordinator(
            () => CurrentGeneration,
            loadPackageIconImageAsync);
        _generationPublisher = new AppPackageGenerationPublisher(
            initialGeneration,
            _uiDispatcher,
            _iconCoordinator,
            resourceAssemblyRegistry,
            AttachFaultForwarder,
            DetachFaultForwarder,
            _quarantineCoordinator.CommitWithFence,
            _quarantineCoordinator.ValidateCandidateForPublication);
        _lifecycleCoordinator = new AppPackageLifecycleCoordinator(
            this,
            _generationBuilder,
            _generationPublisher,
            _iconCoordinator,
            _retirementOwner,
            _lifecycleGate);
    }

    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted
    {
        add => PackageFaultedHandlers += value;
        remove => PackageFaultedHandlers -= value;
    }

    internal event Action<AppPackageUiRecoveryRequest>? PackageUiRecoveryRequested
    {
        add => PackageUiRecoveryRequestedHandlers += value;
        remove => PackageUiRecoveryRequestedHandlers -= value;
    }

    internal int LoadedPackageCount => CurrentGeneration.State.LoadedPackageCount;

    internal int OwnedDisposableCount => CurrentGeneration.State.OwnedDisposableCount;

    internal int LoadContextCount => CurrentGeneration.State.LoadContextCount;

    internal Guid CurrentGenerationId => CurrentGeneration.Id;

    internal int CachedSnapshotCount => _snapshotCache.Count;

    internal int RetirementFailureCount => _retirementOwner.FailureCount;

    internal int QuarantinedRetirementCount => _retirementOwner.QuarantinedCount;

    internal int PendingQuarantineCandidateCount => _quarantineCoordinator.PendingCandidateCount;

    internal int PackageQuarantineCount => _quarantineCoordinator.PackageQuarantineCount;

    internal int AcceptedPackageFaultCount => _quarantineCoordinator.AcceptedPackageFaultCount;

    internal IReadOnlyList<WeakReference> SnapshotLoadContextWeakReferences()
        => CurrentGeneration.State.SnapshotLoadContextWeakReferences();

    internal PackageIconCache PackageIconCache => _iconCoordinator.Cache;

    internal AppPackageLifecycleCoordinator LifecycleCoordinator => _lifecycleCoordinator;

    private AppPackageGeneration CurrentGeneration => _generationPublisher.CurrentGeneration;

    public static async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        CancellationToken cancellationToken = default)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry: null,
            getRuntimeConnectionInfo: null,
            uiDispatcher: null,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PackageViewHostService> CreateForPackagesWithResourceRegistryAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?> getRuntimeConnectionInfo,
        IUiDispatcher uiDispatcher,
        CancellationToken cancellationToken = default,
        IAppPackagePolicy? packagePolicy = null)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry,
            getRuntimeConnectionInfo,
            uiDispatcher,
            cancellationToken, packagePolicy).ConfigureAwait(false);

    private static async Task<PackageViewHostService> CreateForPackagesCoreAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        IUiDispatcher? uiDispatcher,
        CancellationToken cancellationToken,
        IAppPackagePolicy? packagePolicy = null)
    {
        AppPackageSessionDirectories.ScheduleStaleSessionCleanup();
        var sessionFolder = activePackages.Count > 0 ? AppPackageSessionDirectories.CreateSessionFolder() : null;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(uiDispatcher),
            [],
            [],
            [],
            sessionFolder,
            shellViewService: shellViewService,
            settingsNavigationService: settingsNavigationService,
            notificationCenter: notificationCenter,
            backgroundProcessQueue: backgroundProcessQueue,
            resourceAssemblyRegistry: resourceAssemblyRegistry,
            getRuntimeConnectionInfo: getRuntimeConnectionInfo,
            uiDispatcher: uiDispatcher,
            packagePolicy: packagePolicy);

        try
        {
            await hostService.ApplyPackageDeltaAsync(activePackages, packageSources, cancellationToken: cancellationToken).ConfigureAwait(false);
            return hostService;
        }
        catch
        {
            await hostService.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        CancellationToken cancellationToken = default)
        => await _lifecycleCoordinator.ApplyPackageGenerationAsync(
            activePackages,
            packageSources,
            retryDisabledPackageIds,
            commitPresentation: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageGenerationAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Action<IReadOnlyList<ActivePackageDescriptor>>? commitPresentation,
        CancellationToken cancellationToken)
        => await _lifecycleCoordinator.ApplyPackageGenerationAsync(
            activePackages,
            packageSources,
            retryDisabledPackageIds,
            commitPresentation,
            cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageGenerationAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? resetPresentation = null)
        => await _lifecycleCoordinator.ApplyPackageSnapshotAsync(
            snapshot,
            packageSources,
            retryDisabledPackageIds,
            preparePresentation,
            cancellationToken,
            resetPresentation).ConfigureAwait(false);

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        ThrowIfDisposed();
        return AppPackageGenerationBuilder.FilterEnabledPackages(CurrentGeneration, activePackages);
    }

    internal bool IsPackageAppUiQuarantined(string packageId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.State.IsPackageDisabled(packageId);
    }

    internal Task PrewarmPackageIconsAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        CancellationToken cancellationToken)
        => _lifecycleCoordinator.PrewarmCurrentPackageIconsAsync(snapshot, activePackages, cancellationToken);

    internal Task WaitForRetirementsAsync(CancellationToken cancellationToken = default)
        => _retirementOwner.WaitForIdleAsync(cancellationToken);

    public bool TryHandleUnhandledException(Exception exception)
        => TryHandlePackageAttributedException(exception);

    internal bool TryHandleUnobservedTaskException(Exception exception)
        => TryHandlePackageAttributedException(exception);

    internal void RegisterPackageAssemblyForFaultAttribution(
        string packageId,
        System.Reflection.Assembly assembly)
    {
        ThrowIfDisposed();
        CurrentGeneration.Composition.RegisterPackageAssembly(packageId, assembly);
    }

    private bool TryHandlePackageAttributedException(Exception exception)
    {
        ThrowIfDisposed();
        AppPackageFaultAttribution? attribution = null;
        return _faultRouter.TryRoutePackageFailure(
            exception,
            attributedException =>
            {
                attribution = _faultAttribution.ResolveExclusive(attributedException);
                return attribution?.PackageId;
            },
            (packageId, attributedException) => TryBeginPackageQuarantine(
                attribution!,
                packageId,
                "An unhandled App UI fault was attributed exclusively to this package.",
                PackageFailureOrigin.AppUnhandledUi,
                attributedException));
    }

    private void RegisterPackageFaultAttribution(
        Guid generationId,
        string packageId,
        string? contentHash,
        System.Reflection.Assembly assembly)
        => _faultAttribution.Register(
            generationId,
            packageId,
            contentHash,
            assembly);

    public Control? GetOrCreateView(string viewId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.GetOrCreateView(viewId);
    }

    internal bool IsViewPrepared(string viewId, Guid expectedGenerationId)
    {
        ThrowIfDisposed();
        var generation = CurrentGeneration;
        return generation.Id == expectedGenerationId
            && generation.Composition.ViewFacade.IsViewPrepared(viewId);
    }

    internal bool SupportsNavigationPreparation(string viewId, Guid expectedGenerationId)
    {
        ThrowIfDisposed();
        var generation = CurrentGeneration;
        return generation.Id == expectedGenerationId
            && generation.Composition.ViewFacade.SupportsNavigationPreparation(viewId);
    }

    internal async Task<Control?> PreloadViewAsync(
        string viewId,
        Guid expectedGenerationId,
        CancellationToken cancellationToken,
        Func<Control, bool>? retainView = null)
    {
        Control? result = null;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            result = await generation.Composition.ViewFacade
                .WarmupViewAsync(
                    viewId,
                    cancellationToken,
                    retainView is null
                        ? null
                        : control => CurrentGeneration.Id == expectedGenerationId
                            && retainView(control));
            if (CurrentGeneration.Id != expectedGenerationId)
            {
                result = null;
            }
        }).ConfigureAwait(false);
        return result;
    }

    internal async Task<bool> PrepareViewForPresentationAsync(
        string viewId,
        Guid expectedGenerationId,
        Func<Control?, bool> presentView,
        CancellationToken cancellationToken)
    {
        var result = false;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            result = await generation.Composition.ViewFacade.PrepareViewAsync(
                viewId,
                control => CurrentGeneration.Id == expectedGenerationId && presentView(control),
                cancellationToken);
        }).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<Control?> ReloadViewAsync(
        string viewId,
        CancellationToken cancellationToken = default)
    {
        Control? result = null;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            result = await CurrentGeneration.Composition.ViewFacade.ReloadViewAsync(
                viewId,
                cancellationToken);
        }).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<Control?> ReloadViewAsync(
        string viewId,
        Guid expectedGenerationId,
        CancellationToken cancellationToken)
    {
        Control? result = null;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            result = await generation.Composition.ViewFacade.ReloadViewAsync(
                viewId,
                cancellationToken);
            if (CurrentGeneration.Id != expectedGenerationId)
            {
                result = null;
            }
        }).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<bool> InvalidateViewAsync(
        string viewId,
        CancellationToken cancellationToken = default)
    {
        var result = false;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            result = await CurrentGeneration.Composition.ViewFacade.InvalidateViewAsync(
                viewId,
                cancellationToken);
        }).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<bool> InvalidateViewAsync(
        string viewId,
        Guid expectedGenerationId,
        CancellationToken cancellationToken)
    {
        var result = false;
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            result = await generation.Composition.ViewFacade.InvalidateViewAsync(
                viewId,
                cancellationToken);
            result = result && CurrentGeneration.Id == expectedGenerationId;
        }).ConfigureAwait(false);
        return result;
    }

    public async ValueTask NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            await CurrentGeneration.Composition.ViewFacade.NotifyViewNavigatedAsync(
                viewId,
                parameters,
                cancellationToken);
        }).ConfigureAwait(false);
    }

    internal async ValueTask<bool> NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        Guid expectedGenerationId,
        CancellationToken cancellationToken,
        Func<bool>? canStart = null)
    {
        var notified = false;
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            notified = await generation.Composition.ViewFacade.NotifyViewNavigatedAsync(
                viewId,
                parameters,
                cancellationToken,
                () => CurrentGeneration.Id == expectedGenerationId
                    && (canStart is null || canStart()));
            notified = notified && CurrentGeneration.Id == expectedGenerationId;
        }).ConfigureAwait(false);
        return notified;
    }

    internal async ValueTask<bool> PrepareNavigationForPresentationAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        Guid expectedGenerationId,
        Func<Control, bool> stageView,
        Func<Control, bool> presentView,
        Action<Control> unstageView,
        CancellationToken cancellationToken,
        Func<bool>? canStart = null)
    {
        var prepared = false;
        cancellationToken.ThrowIfCancellationRequested();
        await InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId)
            {
                return;
            }

            prepared = await generation.Composition.ViewFacade
                .PrepareNavigationForPresentationAsync(
                    viewId,
                    parameters,
                    stageView,
                    presentView,
                    unstageView,
                    cancellationToken,
                    () => CurrentGeneration.Id == expectedGenerationId
                        && (canStart is null || canStart()));
            prepared = prepared && CurrentGeneration.Id == expectedGenerationId;
        }).ConfigureAwait(false);
        return prepared;
    }

    internal void CancelViewNavigation(string viewId)
        => CurrentGeneration.Composition.ViewFacade.CancelViewNavigation(viewId);

    internal Task CancelViewNavigationAsync(string viewId)
        => CurrentGeneration.Composition.ViewFacade.CancelViewNavigationAsync(viewId);

    internal Task CancelViewNavigationAsync(
        string viewId,
        Guid expectedGenerationId,
        Func<bool>? canStart = null)
        => InvokeNavigationOnUiThreadAsync(async () =>
        {
            var generation = CurrentGeneration;
            if (generation.Id != expectedGenerationId
                || canStart is not null && !canStart())
            {
                return;
            }

            await generation.Composition.ViewFacade.CancelViewNavigationAsync(viewId);
        });

    internal void CancelAllViewNavigations()
        => CurrentGeneration.Composition.ViewFacade.CancelAllViewNavigations();

    internal Task CancelAllViewNavigationsAsync()
        => CurrentGeneration.Composition.ViewFacade.CancelAllViewNavigationsAsync();

    public bool HasSettingsView(string packageId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.HasSettingsView(packageId);
    }

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.ListSettingsViewPackages();
    }

    public Control? GetOrCreateSettingsView(string packageId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.GetOrCreateSettingsView(packageId);
    }

    internal AppPackageSettingsViewNavigationTarget? GetSettingsViewForNavigation(
        string packageId,
        bool requireReplacement)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.GetSettingsViewForNavigation(
            packageId,
            requireReplacement);
    }

    internal Control? CreateHostedViewBoundary(string packageId, string viewId, Control? hostedView)
    {
        ThrowIfDisposed();
        var generation = CurrentGeneration;
        var packageQuarantined = generation.State.IsPackageDisabled(packageId);
        var viewQuarantined = generation.Composition.ViewFacade.IsViewQuarantined(
            packageId,
            viewId);
        if (hostedView is null && !packageQuarantined && !viewQuarantined)
        {
            return null;
        }

        var generationId = generation.Id;
        var contentHash = generation.State.GetPackageContentHash(packageId);
        var boundary = new HostedPackageViewBoundary(
            packageId,
            viewId,
            hostedView,
            (_, _, failedBoundary, exception) =>
                TryHandleBoundaryFailure(
                    generationId,
                    contentHash,
                    packageId,
                    viewId,
                    failedBoundary,
                    exception),
            releasedBoundary =>
            {
                generation.Composition.RecordCleanupOutcome(
                    releasedBoundary.CleanupOutcome);
                UnregisterBoundary(generationId, releasedBoundary);
            });
        RegisterBoundary(generationId, boundary);
        if (packageQuarantined)
        {
            boundary.QuarantinePackage();
        }
        else if (viewQuarantined)
        {
            boundary.QuarantineView();
        }
        return boundary;
    }

    public async ValueTask DisposeAsync()
    {
        await _faultTasks.StopAsync().ConfigureAwait(false);
        using var lifecycle = await _lifecycleGate.TryEnterDisposeAsync().ConfigureAwait(false);
        if (lifecycle is null)
        {
            _faultTasks.Dispose();
            return;
        }

        var generation = CurrentGeneration;
        _generationPublisher.DetachCurrentGeneration();
        await _generationPublisher.PublishEmptyResourcesAsync().ConfigureAwait(false);
        _retirementOwner.Enqueue(generation);
        _iconCoordinator.Dispose();
        await _retirementOwner.DisposeAsync().ConfigureAwait(false);
        _faultTasks.Dispose();

        // Keep the verified cache for the rest of the process; native finalizers can run after package unload.
        GC.SuppressFinalize(this);
    }

    internal Task CollectContentCacheGarbageAsync(CancellationToken cancellationToken)
        => Task.Run(() => _snapshotCache.CollectGarbage(cancellationToken), cancellationToken);

    internal static async Task DisposeOwnedInstanceAsync(object ownedInstance)
        => await AppPackageResourceDisposer.DisposeOwnedInstanceAsync(ownedInstance);

    internal void DisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null)
        => _faultTasks.Run(
            cancellationToken => DisablePackageAsync(
                packageId,
                message,
                origin,
                exception,
                cancellationToken),
            $"disabling package '{packageId}'");

    internal async Task DisablePackageAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = CurrentGeneration;
        _quarantineCoordinator.RememberPackageQuarantine(generation, packageId);
        await generation.Composition.DisablePackageAsync(packageId, message, origin, exception, cancellationToken).ConfigureAwait(false);
    }

    internal AppPackageQuarantineContext CaptureCandidateQuarantineContext(
        IReadOnlyCollection<string>? retryDisabledPackageIds)
        => _quarantineCoordinator.CaptureCandidateContext(retryDisabledPackageIds);

    internal async Task ApplyInheritedQuarantinesAsync(
        AppPackageGeneration generation,
        CancellationToken cancellationToken,
        bool rejectActivatedPackages = true)
        => await _quarantineCoordinator.ApplyInheritedQuarantinesAsync(
            generation,
            cancellationToken,
            rejectActivatedPackages).ConfigureAwait(false);

    internal void QueueCommittedQuarantineCleanup(AppPackageGeneration generation)
        => _faultTasks.Run(
            cancellationToken => ApplyInheritedQuarantinesAsync(
                generation,
                cancellationToken,
                rejectActivatedPackages: false),
            $"finishing inherited quarantines for generation '{generation.Id}'");

    private Task StabilizeCandidateViewAsync(
        AppPackageGeneration generation,
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        Action<Control> stageView,
        CancellationToken cancellationToken)
    {
        var packageId = generation.ViewRegistry.GetPackageId(viewId)
            ?? generation.ActivePackages
                .FirstOrDefault(package => package.Views.Any(view => string.Equals(
                    view.ViewId,
                    viewId,
                    StringComparison.OrdinalIgnoreCase)))
                ?.PackageId;
        if (packageId is not null
            && (generation.State.IsPackageDisabled(packageId)
                || generation.ViewRegistry.IsViewQuarantined(packageId, viewId)))
        {
            return Task.CompletedTask;
        }

        return InvokeNavigationOnUiThreadAsync(async () =>
        {
            if (packageId is not null
                && (generation.State.IsPackageDisabled(packageId)
                    || generation.ViewRegistry.IsViewQuarantined(packageId, viewId)))
            {
                return;
            }

            try
            {
                using var runtimePreparation = generation.Composition.BeginRuntimePreparation();
                cancellationToken.ThrowIfCancellationRequested();
                Control? stagedView = null;
                var prepared = await generation.Composition.ViewFacade.PrepareViewAsync(
                    viewId,
                    view =>
                    {
                        if (view is null)
                        {
                            return false;
                        }

                        stageView(view);
                        stagedView = view;
                        return true;
                    },
                    cancellationToken);
                if (!prepared)
                {
                    throw new InvalidOperationException(
                        $"Selected package view '{viewId}' could not be prepared.");
                }

                await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                    stagedView!,
                    viewId,
                    parameters,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                var packageId = generation.ViewRegistry.GetPackageId(viewId);
                if (packageId is not null)
                {
                    generation.Composition.FaultNotifier.QueueActivationFailure(
                        packageId,
                        candidateReplacement: true,
                        exception);
                }
                throw;
            }
        });
    }

    private string EnsureSessionFolder()
    {
        if (_sessionFolder is not null)
        {
            return _sessionFolder;
        }

        _sessionFolder = AppPackageSessionDirectories.CreateSessionFolder();
        return _sessionFolder;
    }

    private Task InvokeNavigationOnUiThreadAsync(Func<Task> action)
        => _uiDispatcher.CheckAccess()
            ? action()
            : _uiDispatcher.InvokeAsync(action);

    private bool TryRouteKnownViewFailureForGeneration(
        Guid generationId,
        string packageId,
        string viewId,
        string? contentHash,
        Exception exception)
        => _faultRouter.TryRouteKnownViewFailure(
            packageId,
            viewId,
            exception,
            (attributedPackageId, attributedViewId, attributedException) =>
                TryQuarantineView(
                    generationId,
                    contentHash,
                    attributedPackageId,
                    attributedViewId,
                    attributedException));

    internal bool TryHandleKnownViewFailure(
        string packageId,
        string viewId,
        Exception exception)
    {
        var generation = CurrentGeneration;
        return TryRouteKnownViewFailureForGeneration(
            generation.Id,
            packageId,
            viewId,
            generation.State.GetPackageContentHash(packageId),
            exception);
    }

    private bool TryQuarantineView(
        Guid generationId,
        string? contentHash,
        string packageId,
        string viewId,
        Exception exception)
    {
        if (!_quarantineCoordinator.TryQuarantineView(
                generationId,
                contentHash,
                packageId,
                viewId,
                QuarantinePackageBoundaries,
                out var quarantine))
        {
            return false;
        }
        if (quarantine.PackageQuarantined)
        {
            return true;
        }

        var targetGeneration = quarantine.Generation;
        QuarantineViewBoundaries(targetGeneration.Id, viewId);
        if (!quarantine.NewlyQuarantined)
        {
            return true;
        }

        targetGeneration.Composition.FaultNotifier.QueueViewQuarantined(
            this,
            packageId,
            viewId,
            exception);
        var drain = targetGeneration.Composition.ViewFacade.BeginCancelViewOperations(viewId);
        _faultTasks.Run(
            async _ =>
            {
                await drain.ConfigureAwait(false);
                await targetGeneration.ViewRegistry.RemoveCachedViewAsync(
                    packageId,
                    viewId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            $"quarantining package view '{viewId}'");
        return true;
    }

    private bool TryBeginPackageQuarantine(
        AppPackageFaultAttribution attribution,
        string packageId,
        string details,
        PackageFailureOrigin origin,
        Exception exception)
    {
        if (!_quarantineCoordinator.TryBeginPackageQuarantine(
                attribution,
                packageId,
                details,
                origin,
                exception,
                CanAttemptPackageUiRecovery,
                out var quarantine))
        {
            return false;
        }
        if (quarantine is null)
        {
            return true;
        }

        _faultTasks.Observe(
            quarantine.CleanupCompletion,
            $"quarantining package App UI for '{packageId}'");
        var recoveryRequest = quarantine.CanAttemptRecovery
            ? new AppPackageUiRecoveryRequest(
                packageId,
                quarantine.ContentHash!,
                quarantine.FaultedGenerationId,
                quarantine.CleanupCompletion,
                quarantine.FaultNotice)
            : null;
        if (recoveryRequest is null)
        {
            quarantine.FaultNotice?.Publish();
        }
        else
        {
            PublishPackageUiRecoveryRequested(recoveryRequest);
        }
        return true;
    }

    private bool CanAttemptPackageUiRecovery(
        AppPackageGeneration targetGeneration,
        bool isPendingCandidate,
        string? contentHash)
        => !isPendingCandidate
            && ReferenceEquals(targetGeneration, CurrentGeneration)
            && !string.IsNullOrWhiteSpace(contentHash);

    private void PublishPackageUiRecoveryRequested(AppPackageUiRecoveryRequest? request)
    {
        var handlers = PackageUiRecoveryRequestedHandlers;
        if (request is null)
        {
            return;
        }
        if (handlers is null)
        {
            request.FaultNotice?.Publish();
            return;
        }

        var delivered = false;
        foreach (Action<AppPackageUiRecoveryRequest> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(request);
                delivered = true;
            }
            catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                AppSessionLog.WriteError(
                    $"Package App UI recovery subscriber failed for '{request.PackageId}'.",
                    exception);
            }
        }
        if (!delivered)
        {
            request.FaultNotice?.Publish();
        }
    }

    private void RegisterBoundary(Guid generationId, HostedPackageViewBoundary boundary)
    {
        lock (_boundarySyncRoot)
        {
            if (!_boundariesByGeneration.TryGetValue(generationId, out var boundaries))
            {
                boundaries = [];
                _boundariesByGeneration.Add(generationId, boundaries);
            }
            boundaries.RemoveAll(static reference => !reference.TryGetTarget(out _));
            boundaries.Add(new WeakReference<HostedPackageViewBoundary>(boundary));
        }
    }

    private void UnregisterBoundary(
        Guid generationId,
        HostedPackageViewBoundary boundary)
    {
        lock (_boundarySyncRoot)
        {
            if (!_boundariesByGeneration.TryGetValue(generationId, out var boundaries))
            {
                return;
            }

            boundaries.RemoveAll(reference =>
                !reference.TryGetTarget(out var candidate)
                || ReferenceEquals(candidate, boundary));
            if (boundaries.Count == 0)
            {
                _boundariesByGeneration.Remove(generationId);
            }
        }
    }

    private void QuarantineViewBoundaries(Guid generationId, string viewId)
        => QueueBoundaryQuarantine(() =>
        {
            foreach (var boundary in SnapshotBoundaries(generationId))
            {
                if (string.Equals(boundary.ViewId, viewId, StringComparison.OrdinalIgnoreCase))
                {
                    RecordBoundaryCleanupOutcome(
                        generationId,
                        boundary.QuarantineView());
                }
            }
        });

    private void QuarantinePackageBoundaries(Guid generationId, string packageId)
        => QueueBoundaryQuarantine(() =>
        {
            _ = QuarantinePackageBoundariesCore(generationId, packageId);
        });

    private async Task<AppPackageUiCleanupOutcome> QuarantinePackageBoundariesAsync(
        Guid generationId,
        string packageId)
    {
        if (_uiDispatcher.CheckAccess())
        {
            return QuarantinePackageBoundariesCore(generationId, packageId);
        }

        return await _uiDispatcher.InvokeAsync(
            () => QuarantinePackageBoundariesCore(generationId, packageId));
    }

    private AppPackageUiCleanupOutcome QuarantinePackageBoundariesCore(
        Guid generationId,
        string packageId)
    {
        var cleanupOutcome = AppPackageUiCleanupOutcome.Proven;
        foreach (var boundary in SnapshotBoundaries(generationId))
        {
            if (string.Equals(
                boundary.PackageId,
                packageId,
                StringComparison.OrdinalIgnoreCase)
                && boundary.QuarantinePackage() == AppPackageUiCleanupOutcome.Unproven)
            {
                cleanupOutcome = AppPackageUiCleanupOutcome.Unproven;
            }
        }
        RecordBoundaryCleanupOutcome(generationId, cleanupOutcome);
        return cleanupOutcome;
    }

    private void RecordBoundaryCleanupOutcome(
        Guid generationId,
        AppPackageUiCleanupOutcome cleanupOutcome)
        => _quarantineCoordinator.RecordCleanupOutcome(
            generationId,
            cleanupOutcome);

    private void QueueBoundaryQuarantine(Action quarantine)
    {
        if (_uiDispatcher.CheckAccess())
        {
            quarantine();
            return;
        }

        try
        {
            _faultTasks.Observe(
                _uiDispatcher.InvokeAsync(quarantine),
                "replacing quarantined package view boundaries");
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                "Failed to queue replacement of quarantined package view boundaries.",
                exception);
        }
    }

    private HostedPackageViewBoundary[] SnapshotBoundaries(Guid generationId)
    {
        lock (_boundarySyncRoot)
        {
            if (!_boundariesByGeneration.TryGetValue(generationId, out var boundaries))
            {
                return [];
            }

            var result = boundaries
                .Select(reference => reference.TryGetTarget(out var boundary) ? boundary : null)
                .OfType<HostedPackageViewBoundary>()
                .ToArray();
            boundaries.RemoveAll(static reference => !reference.TryGetTarget(out _));
            if (boundaries.Count == 0)
            {
                _boundariesByGeneration.Remove(generationId);
            }
            return result;
        }
    }

    private bool TryHandleBoundaryFailure(
        Guid generationId,
        string? contentHash,
        string packageId,
        string viewId,
        HostedPackageViewBoundary boundary,
        Exception exception)
    {
        if (!AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            return false;
        }
        var handled = TryRouteKnownViewFailureForGeneration(
            generationId,
            packageId,
            viewId,
            contentHash,
            exception);
        if (handled && CurrentGeneration.State.IsPackageDisabled(packageId))
        {
            boundary.QuarantinePackage();
        }
        return handled;
    }

    private void ThrowIfDisposed()
        => _lifecycleGate.ThrowIfDisposed();

    internal void ThrowIfDisposedForLifecycle()
        => ThrowIfDisposed();

    private void AttachFaultForwarder(AppPackageGeneration generation)
    {
        generation.Composition.FaultNotifier.PackageFaulted += Composition_OnPackageFaulted;
        generation.Composition.PackageStateChanged += Composition_OnPackageStateChanged;
    }

    private void DetachFaultForwarder(AppPackageGeneration generation)
    {
        generation.Composition.FaultNotifier.PackageFaulted -= Composition_OnPackageFaulted;
        generation.Composition.PackageStateChanged -= Composition_OnPackageStateChanged;
        lock (_boundarySyncRoot)
        {
            _boundariesByGeneration.Remove(generation.Id);
        }
    }

    private void Composition_OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
    {
        var handlers = PackageFaultedHandlers;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PackageViewHostFaultEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, e);
            }
            catch (Exception exception) when (
                AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                AppSessionLog.WriteError(
                    $"Package view host fault subscriber failed for '{e.PackageId}'.",
                    exception);
            }
        }
    }

    private void Composition_OnPackageStateChanged()
        => _generationPublisher.PublishCurrentResources();

    internal sealed class AppPackagePresentationCandidate(
        PackageViewHostService owner,
        AppPackageGeneration generation)
    {
        public async Task StabilizeViewAsync(
            string viewId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            Action<Control>? stageView = null,
            CancellationToken cancellationToken = default)
        {
            await owner.ApplyInheritedQuarantinesAsync(
                generation,
                cancellationToken).ConfigureAwait(false);
            await owner.StabilizeCandidateViewAsync(
                generation,
                viewId,
                parameters,
                stageView ?? (static _ => { }),
                cancellationToken).ConfigureAwait(false);
        }
    }

}
