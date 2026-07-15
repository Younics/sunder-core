using Avalonia.Controls;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.App.Services;

public sealed class PackageViewHostFaultEventArgs(string packageId, string message, PackageFailureOrigin origin) : EventArgs
{
    public string PackageId { get; } = packageId;

    public string Message { get; } = message;

    public PackageFailureOrigin Origin { get; } = origin;
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
        faultReporter: null,
        sessionFolder: null,
        backgroundProcessQueue: null);

    private readonly AppPackageLifecycleGate _lifecycleGate = new(nameof(PackageViewHostService));
    private readonly IUiDispatcher _uiDispatcher;
    private readonly AppPackageSnapshotCache _snapshotCache;
    private readonly AppPackageGenerationBuilder _generationBuilder;
    private readonly AppPackageGenerationPublisher _generationPublisher;
    private readonly PackageIconGenerationCoordinator _iconCoordinator;
    private readonly AppPackageGenerationRetirementQueue _retirementOwner;
    private readonly AppPackageLifecycleCoordinator _lifecycleCoordinator;
    private string? _sessionFolder;
    private event EventHandler<PackageViewHostFaultEventArgs>? PackageFaultedHandlers;

    internal PackageViewHostService(
        AppPackageViewRegistry viewRegistry,
        HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts,
        PackageRuntimeFaultReporter? faultReporter,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        AppPackageExtensionCatalog? extensionCatalog = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        AppPackageSessionService? packageSessionService = null,
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
        string? packageContentCacheRoot = null)
    {
        _sessionFolder = sessionFolder;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;

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
        _retirementOwner = new AppPackageGenerationRetirementQueue(
            retireGenerationAsync,
            retirementBudget,
            retirementDrainBudget);
        _generationBuilder = new AppPackageGenerationBuilder(
            this,
            _snapshotCache,
            EnsureSessionFolder,
            faultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            getRuntimeConnectionInfo,
            _uiDispatcher);
        var initialGeneration = _generationBuilder.CreateInitialGeneration(
            viewRegistry,
            disabledPackageIds,
            ownedDisposables,
            loadContexts,
            sharedAssemblyRegistry,
            extensionCatalog);
        _iconCoordinator = new PackageIconGenerationCoordinator(
            () => CurrentGeneration,
            loadPackageIconImageAsync);
        _generationPublisher = new AppPackageGenerationPublisher(
            initialGeneration,
            _uiDispatcher,
            _iconCoordinator,
            resourceAssemblyRegistry,
            AttachFaultForwarder,
            DetachFaultForwarder);
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
        PackageRuntimeFaultReporter? faultReporter = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        AppPackageSessionService? packageSessionService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        CancellationToken cancellationToken = default)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            faultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry: null,
            getRuntimeConnectionInfo: null,
            uiDispatcher: null,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PackageViewHostService> CreateForPackagesWithResourceRegistryAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        AppPackageSessionService? packageSessionService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?> getRuntimeConnectionInfo,
        IUiDispatcher uiDispatcher,
        CancellationToken cancellationToken = default)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            faultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry,
            getRuntimeConnectionInfo,
            uiDispatcher,
            cancellationToken).ConfigureAwait(false);

    private static async Task<PackageViewHostService> CreateForPackagesCoreAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        AppPackageSessionService? packageSessionService,
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
            faultReporter,
            sessionFolder,
            shellViewService: shellViewService,
            settingsNavigationService: settingsNavigationService,
            packageSessionService: packageSessionService,
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
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken)
        => await _lifecycleCoordinator.ApplyPackageSnapshotAsync(
            snapshot,
            retryDisabledPackageIds,
            preparePresentation,
            cancellationToken).ConfigureAwait(false);

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        ThrowIfDisposed();
        return AppPackageGenerationBuilder.FilterEnabledPackages(CurrentGeneration, activePackages);
    }

    internal Task PrewarmPackageIconsAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        CancellationToken cancellationToken)
        => _lifecycleCoordinator.PrewarmCurrentPackageIconsAsync(snapshot, activePackages, cancellationToken);

    internal Task WaitForRetirementsAsync(CancellationToken cancellationToken = default)
        => _retirementOwner.WaitForIdleAsync(cancellationToken);

    public bool TryHandleUnhandledException(Exception exception)
    {
        ThrowIfDisposed();
        var packageId = CurrentGeneration.Composition.AssemblyTracker.ResolvePackageId(exception);
        if (packageId is null)
        {
            return false;
        }

        DisablePackage(
            packageId,
            $"Unhandled package UI exception: {exception.Message}",
            PackageFailureOrigin.AppUnhandledUi,
            exception);
        return true;
    }

    public Control? GetOrCreateView(string viewId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.GetOrCreateView(viewId);
    }

    public Control? ReloadView(string viewId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.ReloadView(viewId);
    }

    public bool InvalidateView(string viewId)
    {
        ThrowIfDisposed();
        return CurrentGeneration.Composition.ViewFacade.InvalidateView(viewId);
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

    internal void CancelViewNavigation(string viewId)
        => CurrentGeneration.Composition.ViewFacade.CancelViewNavigation(viewId);

    internal void CancelAllViewNavigations()
        => CurrentGeneration.Composition.ViewFacade.CancelAllViewNavigations();

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

    public async Task<IReadOnlyList<string>> NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (appliedContributions.Count == 0)
        {
            return [];
        }

        var warnings = new List<string>();
        var stackContributions = CurrentGeneration.Composition.ExtensionCatalog.GetExtensionContributions(SunderStackExtensionPoints.StackImportAppliedHandlers);
        foreach (var applied in appliedContributions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handlers = stackContributions
                .Where(contribution => string.Equals(contribution.PackageId, applied.OwnerPackageId, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(contribution.Contribution.ContributorId, applied.ContributorId, StringComparison.OrdinalIgnoreCase))
                .Select(contribution => contribution.Contribution)
                .ToArray();
            if (handlers.Length == 0)
            {
                continue;
            }

            var context = new StackImportAppliedContext(
                applied.OwnerPackageId,
                applied.ContributorId,
                applied.FragmentIds,
                applied.ImportedItems
                    .Select(item => new StackImportedItem(item.ItemId, item.DisplayName, item.Kind))
                    .ToArray());
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.OnStackImportAppliedAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"Package '{applied.OwnerPackageId}' did not refresh imported Stack data: {ex.Message}";
                    warnings.Add(message);
                    AppSessionLog.WriteError(message, ex);
                }
            }
        }

        return warnings;
    }

    internal Control? CreateHostedViewBoundary(string packageId, string viewId, Control? hostedView)
    {
        ThrowIfDisposed();
        return hostedView is null
            ? null
            : new HostedPackageViewBoundary(packageId, viewId, hostedView, ReportHostedViewFailure);
    }

    public async ValueTask DisposeAsync()
    {
        using var lifecycle = await _lifecycleGate.TryEnterDisposeAsync().ConfigureAwait(false);
        if (lifecycle is null)
        {
            return;
        }

        var generation = CurrentGeneration;
        _generationPublisher.DetachCurrentGeneration();
        await _generationPublisher.PublishEmptyResourcesAsync().ConfigureAwait(false);
        _retirementOwner.Enqueue(generation);
        _iconCoordinator.Dispose();
        await _retirementOwner.DisposeAsync().ConfigureAwait(false);

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
        => CurrentGeneration.Composition.DisablePackage(packageId, message, origin, exception);

    internal async Task DisablePackageAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await CurrentGeneration.Composition.DisablePackageAsync(packageId, message, origin, exception, cancellationToken).ConfigureAwait(false);
    }

    private Task StabilizeCandidateViewAsync(
        AppPackageGeneration generation,
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        Action<Control> stageView,
        CancellationToken cancellationToken)
        => InvokeNavigationOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = generation.Composition.ViewFacade.GetOrCreateView(viewId);
            if (view is null)
            {
                throw new InvalidOperationException($"Selected package view '{viewId}' could not be prepared.");
            }

            stageView(view);
            await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                view,
                viewId,
                parameters,
                cancellationToken);
        });

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

    private void ReportHostedViewFailure(string packageId, string viewId, Exception exception)
        => DisablePackage(
            packageId,
            $"Hosted package view '{viewId}' failed: {exception.Message}",
            PackageFailureOrigin.AppHostedView,
            exception);

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
    }

    private void Composition_OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
        => PackageFaultedHandlers?.Invoke(this, e);

    private void Composition_OnPackageStateChanged()
        => _generationPublisher.PublishCurrentResources();

    internal sealed class AppPackagePresentationCandidate(
        PackageViewHostService owner,
        AppPackageGeneration generation)
    {
        public Task StabilizeViewAsync(
            string viewId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            Action<Control>? stageView = null,
            CancellationToken cancellationToken = default)
            => owner.StabilizeCandidateViewAsync(
                generation,
                viewId,
                parameters,
                stageView ?? (static _ => { }),
                cancellationToken);
    }
}
