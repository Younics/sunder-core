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

internal sealed record AppPackageQuarantineContext(
    long BaselineRevision,
    IReadOnlySet<string> RetryPackageQuarantineIds,
    IReadOnlySet<string> RetryViewQuarantinePackageIds)
{
    public static AppPackageQuarantineContext Empty { get; } = new(
        0,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IgnoresPackage(string packageId, long quarantineRevision)
        => quarantineRevision <= BaselineRevision
            && RetryPackageQuarantineIds.Contains(packageId);

    public bool IgnoresView(string packageId, long quarantineRevision)
        => quarantineRevision <= BaselineRevision
            && RetryViewQuarantinePackageIds.Contains(packageId);
}

internal sealed class AppPackageCandidateRebuildRequiredException(
    IReadOnlyList<string> packageIds)
    : Exception("An activated App package candidate was quarantined and must be rebuilt.")
{
    public IReadOnlyList<string> PackageIds { get; } = packageIds;
}

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
    private readonly object _boundarySyncRoot = new();
    private readonly Dictionary<Guid, List<WeakReference<HostedPackageViewBoundary>>> _boundariesByGeneration = [];
    private readonly object _quarantineCommitSyncRoot = new();
    private readonly Dictionary<string, PackageQuarantineRecord> _packageQuarantines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViewQuarantineRecord> _viewQuarantines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, PendingCandidateRegistration> _pendingCandidates = [];
    private readonly HashSet<string> _acceptedPackageFaultGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Exception> _propagateFatalCleanupFault;
    private long _quarantineRevision;
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
        Action<Exception>? fatalCleanupFaultHandler = null)
    {
        _sessionFolder = sessionFolder;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _propagateFatalCleanupFault = fatalCleanupFaultHandler
            ?? AppPackageFatalCleanupPropagation.PostToDispatcher;
        _faultTasks = new OwnedTaskObserver(
            nameof(PackageViewHostService),
            _propagateFatalCleanupFault);

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

            UnregisterPendingCandidate(generation);
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
            RegisterPendingCandidate,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            getRuntimeConnectionInfo,
            _uiDispatcher);
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
            CommitWithQuarantineFence,
            ValidateCandidateForPublication);
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
        CancellationToken cancellationToken = default)
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
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
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
            uiDispatcher: uiDispatcher);

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

    private void RegisterPendingCandidate(
        AppPackageGeneration generation,
        bool rejectOnFault)
    {
        lock (_quarantineCommitSyncRoot)
        {
            _pendingCandidates.Add(
                generation.Id,
                new PendingCandidateRegistration(
                    new WeakReference<AppPackageGeneration>(generation),
                    rejectOnFault));
        }
    }

    internal void UnregisterPendingCandidate(AppPackageGeneration generation)
    {
        lock (_quarantineCommitSyncRoot)
        {
            RemovePendingCandidate(generation);
        }
    }

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
        RememberPackageQuarantine(generation, packageId);
        await generation.Composition.DisablePackageAsync(packageId, message, origin, exception, cancellationToken).ConfigureAwait(false);
    }

    internal AppPackageQuarantineContext CaptureCandidateQuarantineContext(
        IReadOnlyCollection<string>? retryDisabledPackageIds)
    {
        lock (_quarantineCommitSyncRoot)
        {
            var retryPackageIds = retryDisabledPackageIds is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    retryDisabledPackageIds,
                    StringComparer.OrdinalIgnoreCase);
            var retryViewPackageIds = retryDisabledPackageIds
                is AppPackageQuarantineRetryIntent { PreserveKnownViewQuarantines: true }
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(retryPackageIds, StringComparer.OrdinalIgnoreCase);
            return new AppPackageQuarantineContext(
                _quarantineRevision,
                retryPackageIds,
                retryViewPackageIds);
        }
    }

    internal async Task ApplyInheritedQuarantinesAsync(
        AppPackageGeneration generation,
        CancellationToken cancellationToken,
        bool rejectActivatedPackages = true)
    {
        QuarantineInheritance inheritance;
        lock (_quarantineCommitSyncRoot)
        {
            if (rejectActivatedPackages)
            {
                ThrowIfCandidateCannotCommit(generation);
            }
            inheritance = ApplyMatchingQuarantines(
                generation,
                commitCandidate: false);
        }

        foreach (var packageId in inheritance.PackageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await generation.Composition.ApplyInheritedPackageQuarantineAsync(packageId)
                .ConfigureAwait(false);
        }
        foreach (var quarantine in inheritance.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drain = generation.Composition.ViewFacade.BeginCancelViewOperations(
                quarantine.ViewId);
            await drain.ConfigureAwait(false);
            await generation.ViewRegistry.RemoveCachedViewAsync(
                quarantine.PackageId,
                quarantine.ViewId,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal void QueueCommittedQuarantineCleanup(AppPackageGeneration generation)
        => _faultTasks.Run(
            cancellationToken => ApplyInheritedQuarantinesAsync(
                generation,
                cancellationToken,
                rejectActivatedPackages: false),
            $"finishing inherited quarantines for generation '{generation.Id}'");

    private void CommitWithQuarantineFence(
        AppPackageGeneration candidate,
        Action commit)
    {
        lock (_quarantineCommitSyncRoot)
        {
            ThrowIfCandidateCannotCommit(candidate);
            var inheritance = ApplyMatchingQuarantines(
                candidate,
                commitCandidate: true);
            commit();
            RemovePendingCandidate(candidate);
            foreach (var quarantine in inheritance.PackageRecordsToClear)
            {
                if (_packageQuarantines.TryGetValue(
                        quarantine.PackageId,
                        out var current)
                    && current.Revision == quarantine.Revision)
                {
                    _packageQuarantines.Remove(quarantine.PackageId);
                }
            }
            foreach (var quarantine in inheritance.ViewRecordsToClear)
            {
                var key = GetViewQuarantineKey(
                    quarantine.PackageId,
                    quarantine.ViewId);
                if (_viewQuarantines.TryGetValue(key, out var current)
                    && current.Revision == quarantine.Revision)
                {
                    _viewQuarantines.Remove(key);
                }
            }
        }
    }

    private void ValidateCandidateForPublication(
        AppPackageGeneration candidate)
    {
        lock (_quarantineCommitSyncRoot)
        {
            ThrowIfCandidateCannotCommit(candidate);
        }
    }

    private void ThrowIfCandidateCannotCommit(AppPackageGeneration generation)
    {
        if (_pendingCandidates.TryGetValue(generation.Id, out var pending)
            && pending.Generation.TryGetTarget(out var registeredGeneration)
            && ReferenceEquals(registeredGeneration, generation)
            && pending.FaultedPackageIds.Count > 0)
        {
            var packageIds = pending.FaultedPackageIds.ToArray();
            if (pending.RejectOnFault)
            {
                throw CreateCandidateFaultException(packageIds);
            }

            throw new AppPackageCandidateRebuildRequiredException(packageIds);
        }

        ThrowIfActivatedPackageRequiresRebuild(generation);
    }

    private void ThrowIfActivatedPackageRequiresRebuild(
        AppPackageGeneration generation)
    {
        var matchingQuarantines = _packageQuarantines
            .Where(entry => IsMatchingPackageQuarantine(
                generation,
                entry.Key,
                entry.Value))
            .Where(entry => generation.State.HasActivatedPackage(entry.Key))
            .ToArray();
        if (matchingQuarantines.Any(static entry => entry.Value.RejectCandidate))
        {
            throw CreateCandidateFaultException(
                matchingQuarantines.Select(static entry => entry.Key));
        }
        if (matchingQuarantines.Length > 0)
        {
            throw new AppPackageCandidateRebuildRequiredException(
                matchingQuarantines.Select(static entry => entry.Key).ToArray());
        }
    }

    private static InvalidOperationException CreateCandidateFaultException(
        IEnumerable<string> packageIds)
        => new(
            $"App package candidate failed after an asynchronous managed fault from '{string.Join("', '", packageIds)}'.");

    private static bool IsMatchingPackageQuarantine(
        AppPackageGeneration generation,
        string packageId,
        PackageQuarantineRecord quarantine)
        => !generation.QuarantineContext.IgnoresPackage(packageId, quarantine.Revision)
            && generation.State.GetPackageContentHash(packageId) is { } contentHash
            && string.Equals(
                contentHash,
                quarantine.ContentHash,
                StringComparison.OrdinalIgnoreCase);

    private QuarantineInheritance ApplyMatchingQuarantines(
        AppPackageGeneration generation,
        bool commitCandidate)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageRecordsToClear = new List<PackageQuarantineRecord>();
        foreach (var (packageId, quarantine) in _packageQuarantines.ToArray())
        {
            if (generation.QuarantineContext.IgnoresPackage(
                    packageId,
                    quarantine.Revision))
            {
                if (commitCandidate)
                {
                    packageRecordsToClear.Add(quarantine);
                }
                continue;
            }

            var candidateContentHash = generation.State.GetPackageContentHash(packageId);
            if (string.IsNullOrWhiteSpace(candidateContentHash))
            {
                continue;
            }
            if (!string.Equals(
                    candidateContentHash,
                    quarantine.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (commitCandidate)
                {
                    packageRecordsToClear.Add(quarantine);
                }
                continue;
            }

            generation.State.TryMarkPackageDisabled(packageId);
            packageIds.Add(packageId);
        }

        var views = new List<ViewQuarantineRecord>();
        var viewRecordsToClear = new List<ViewQuarantineRecord>();
        foreach (var quarantine in _viewQuarantines.Values.ToArray())
        {
            if (generation.QuarantineContext.IgnoresView(
                    quarantine.PackageId,
                    quarantine.Revision))
            {
                if (commitCandidate)
                {
                    viewRecordsToClear.Add(quarantine);
                }
                continue;
            }

            var candidateContentHash = generation.State.GetPackageContentHash(
                quarantine.PackageId);
            if (string.IsNullOrWhiteSpace(candidateContentHash))
            {
                continue;
            }
            if (!string.Equals(
                    candidateContentHash,
                    quarantine.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (commitCandidate)
                {
                    viewRecordsToClear.Add(quarantine);
                }
                continue;
            }
            if (packageIds.Contains(quarantine.PackageId))
            {
                continue;
            }
            if (generation.ViewRegistry.TryQuarantineView(
                    quarantine.PackageId,
                    quarantine.ViewId,
                    out _))
            {
                views.Add(quarantine);
            }
        }

        return new QuarantineInheritance(
            packageIds.ToArray(),
            views.ToArray(),
            packageRecordsToClear,
            viewRecordsToClear);
    }

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
        AppPackageGeneration targetGeneration;
        bool newlyQuarantined;
        lock (_quarantineCommitSyncRoot)
        {
            var currentGeneration = CurrentGeneration;
            if (currentGeneration.Id == generationId)
            {
                targetGeneration = currentGeneration;
            }
            else if (!string.IsNullOrWhiteSpace(contentHash)
                && string.Equals(
                    currentGeneration.State.GetPackageContentHash(packageId),
                    contentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetGeneration = currentGeneration;
            }
            else
            {
                return false;
            }
            if (targetGeneration.State.IsPackageDisabled(packageId))
            {
                QuarantinePackageBoundaries(targetGeneration.Id, packageId);
                return true;
            }
            if (!targetGeneration.ViewRegistry.TryQuarantineView(
                    packageId,
                    viewId,
                    out newlyQuarantined))
            {
                return false;
            }

            if (newlyQuarantined)
            {
                RememberViewQuarantine(targetGeneration, packageId, viewId);
            }
        }

        QuarantineViewBoundaries(targetGeneration.Id, viewId);
        if (!newlyQuarantined)
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
        Task<AppPackageUiCleanupOutcome> completion;
        AppPackageGeneration targetGeneration;
        AppPackageUiRecoveryRequest? recoveryRequest = null;
        lock (_quarantineCommitSyncRoot)
        {
            var faultedContentHash = attribution.ContentHash;
            PendingCandidateRegistration? pendingCandidate = null;
            if (TryResolvePendingCandidate(
                    attribution,
                    packageId,
                    out var resolvedPendingCandidate,
                    out var resolvedCandidate))
            {
                pendingCandidate = resolvedPendingCandidate;
                targetGeneration = resolvedCandidate;
                if (pendingCandidate.FaultedPackageIds.Contains(packageId))
                {
                    return true;
                }
            }
            else
            {
                var currentGeneration = CurrentGeneration;
                if (currentGeneration.Id == attribution.GenerationId)
                {
                    targetGeneration = currentGeneration;
                }
                else if (!string.IsNullOrWhiteSpace(faultedContentHash)
                    && string.Equals(
                        currentGeneration.State.GetPackageContentHash(packageId),
                        faultedContentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    targetGeneration = currentGeneration;
                }
                else
                {
                    return false;
                }
            }

            var acceptedFaultKey = pendingCandidate is null
                && ReferenceEquals(targetGeneration, CurrentGeneration)
                    ? CreateAcceptedPackageFaultKey(
                        attribution.GenerationId,
                        packageId,
                        faultedContentHash)
                    : null;
            if (acceptedFaultKey is not null
                && _acceptedPackageFaultGenerations.Contains(acceptedFaultKey))
            {
                return true;
            }

            var quarantineKey = packageId;
            _packageQuarantines.TryGetValue(quarantineKey, out var previousQuarantine);
            if (!string.IsNullOrWhiteSpace(faultedContentHash))
            {
                _packageQuarantines[quarantineKey] = new PackageQuarantineRecord(
                    packageId,
                    faultedContentHash,
                    ++_quarantineRevision,
                    RejectCandidate: pendingCandidate?.RejectOnFault == true);
            }

            pendingCandidate?.FaultedPackageIds.Add(packageId);
            var reportedException = pendingCandidate is null
                ? exception
                : new InvalidOperationException(exception.ToString());

            if (!targetGeneration.Composition.TryBeginDisablePackage(
                    packageId,
                    details,
                    origin,
                    reportedException,
                    out completion))
            {
                if (previousQuarantine is null)
                {
                    _packageQuarantines.Remove(quarantineKey);
                }
                else
                {
                    _packageQuarantines[quarantineKey] = previousQuarantine;
                }
                pendingCandidate?.FaultedPackageIds.Remove(packageId);
                return false;
            }

            if (acceptedFaultKey is not null)
            {
                _acceptedPackageFaultGenerations.Add(acceptedFaultKey);
            }

            if (pendingCandidate is null
                && ReferenceEquals(targetGeneration, CurrentGeneration)
                && !string.IsNullOrWhiteSpace(faultedContentHash))
            {
                recoveryRequest = new AppPackageUiRecoveryRequest(
                    packageId,
                    faultedContentHash,
                    attribution.GenerationId,
                    completion);
            }
        }

        _faultTasks.Observe(completion, $"quarantining package App UI for '{packageId}'");
        PublishPackageUiRecoveryRequested(recoveryRequest);
        return true;
    }

    private void PublishPackageUiRecoveryRequested(AppPackageUiRecoveryRequest? request)
    {
        var handlers = PackageUiRecoveryRequestedHandlers;
        if (request is null || handlers is null)
        {
            return;
        }

        foreach (Action<AppPackageUiRecoveryRequest> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(request);
            }
            catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                AppSessionLog.WriteError(
                    $"Package App UI recovery subscriber failed for '{request.PackageId}'.",
                    exception);
            }
        }
    }

    private bool TryResolvePendingCandidate(
        AppPackageFaultAttribution attribution,
        string packageId,
        out PendingCandidateRegistration pending,
        out AppPackageGeneration generation)
    {
        pending = null!;
        generation = null!;
        if (attribution.GenerationId == Guid.Empty
            || !_pendingCandidates.TryGetValue(attribution.GenerationId, out var candidate))
        {
            return false;
        }
        if (!candidate.Generation.TryGetTarget(out var registeredGeneration))
        {
            _pendingCandidates.Remove(attribution.GenerationId);
            return false;
        }
        generation = registeredGeneration;

        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash)
            || !string.Equals(
                contentHash,
                attribution.ContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            generation = null!;
            return false;
        }

        pending = candidate;
        return true;
    }

    private void RemovePendingCandidate(AppPackageGeneration generation)
    {
        if (!_pendingCandidates.TryGetValue(generation.Id, out var pending)
            || pending.Generation.TryGetTarget(out var registeredGeneration)
            && !ReferenceEquals(registeredGeneration, generation))
        {
            return;
        }

        _pendingCandidates.Remove(generation.Id);
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
    {
        if (cleanupOutcome == AppPackageUiCleanupOutcome.Proven)
        {
            return;
        }

        lock (_quarantineCommitSyncRoot)
        {
            var currentGeneration = CurrentGeneration;
            if (currentGeneration.Id == generationId)
            {
                currentGeneration.Composition.RecordCleanupOutcome(cleanupOutcome);
                return;
            }
            if (_pendingCandidates.TryGetValue(generationId, out var pending)
                && pending.Generation.TryGetTarget(out var pendingGeneration))
            {
                pendingGeneration.Composition.RecordCleanupOutcome(cleanupOutcome);
            }
        }
    }

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

    private void RememberViewQuarantine(
        AppPackageGeneration generation,
        string packageId,
        string viewId)
    {
        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        _viewQuarantines[GetViewQuarantineKey(packageId, viewId)] = new ViewQuarantineRecord(
            packageId,
            viewId,
            contentHash,
            ++_quarantineRevision);
    }

    private void RememberPackageQuarantine(
        AppPackageGeneration generation,
        string packageId)
    {
        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        lock (_quarantineCommitSyncRoot)
        {
            _packageQuarantines[packageId] = new PackageQuarantineRecord(
                packageId,
                contentHash,
                ++_quarantineRevision);
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

    private static string GetViewQuarantineKey(string packageId, string viewId)
        => $"{packageId}\0{viewId}";

    private static string? CreateAcceptedPackageFaultKey(
        Guid generationId,
        string packageId,
        string? contentHash)
        => generationId == Guid.Empty || string.IsNullOrWhiteSpace(contentHash)
            ? null
            : $"{generationId:N}\0{packageId.Trim()}\0{contentHash.Trim()}";

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

    private sealed record ViewQuarantineRecord(
        string PackageId,
        string ViewId,
        string ContentHash,
        long Revision);

    private sealed record PackageQuarantineRecord(
        string PackageId,
        string ContentHash,
        long Revision,
        bool RejectCandidate = false);

    private sealed class PendingCandidateRegistration(
        WeakReference<AppPackageGeneration> generation,
        bool rejectOnFault)
    {
        public WeakReference<AppPackageGeneration> Generation { get; } = generation;

        public bool RejectOnFault { get; } = rejectOnFault;

        public HashSet<string> FaultedPackageIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record QuarantineInheritance(
        IReadOnlyList<string> PackageIds,
        IReadOnlyList<ViewQuarantineRecord> Views,
        IReadOnlyList<PackageQuarantineRecord> PackageRecordsToClear,
        IReadOnlyList<ViewQuarantineRecord> ViewRecordsToClear);
}
