using Avalonia.Controls;
using Sunder.App.Views.Controls;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

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
        sessionFolder: null,
        backgroundProcessQueue: null);

    private readonly AppPackageLifecycleGate _lifecycleGate = new(nameof(PackageViewHostService));
    private readonly OwnedTaskObserver _faultTasks = new(nameof(PackageViewHostService));
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
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        AppPackageExtensionCatalog? extensionCatalog = null,
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
            DisablePackageForGeneration,
            DisableExtensionOwnerForGeneration,
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
        return hostedView is null
            ? null
            : new HostedPackageViewBoundary(packageId, viewId, hostedView, ReportHostedViewFailure);
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

    private void DisablePackageForGeneration(
        Guid generationId,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        var faultedGeneration = CurrentGeneration;
        if (faultedGeneration.Id != generationId)
        {
            return;
        }

        var faultedContentHash = faultedGeneration.State.GetLoadedPackage(packageId)?.Source.ContentHash;
        _faultTasks.Run(
            async cancellationToken =>
            {
                using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
                var currentGeneration = CurrentGeneration;
                if (currentGeneration.Id != generationId
                    && (faultedContentHash is null
                        || !string.Equals(
                            currentGeneration.State.GetLoadedPackage(packageId)?.Source.ContentHash,
                            faultedContentHash,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                await currentGeneration.Composition.DisablePackageAsync(
                    packageId,
                    message,
                    origin,
                    exception,
                    cancellationToken).ConfigureAwait(false);
            },
            $"disabling package '{packageId}' for generation '{generationId:N}'");
    }

    private void DisableExtensionOwnerForGeneration(
        Guid generationId,
        PackageExtensionOwnerToken ownerToken,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        var faultedGeneration = CurrentGeneration;
        if (faultedGeneration.Id != generationId
            || !faultedGeneration.Composition.ExtensionCatalog.IsActiveOwner(ownerToken))
        {
            return;
        }

        var packageId = ownerToken.PackageId;
        _faultTasks.Run(
            async cancellationToken =>
            {
                using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
                var currentGeneration = CurrentGeneration;
                if (currentGeneration.Id != generationId
                    || !currentGeneration.Composition.ExtensionCatalog.IsActiveOwner(ownerToken))
                {
                    return;
                }

                await currentGeneration.Composition.DisablePackageAsync(
                    packageId,
                    message,
                    origin,
                    exception,
                    cancellationToken).ConfigureAwait(false);
            },
            $"disabling extension owner '{packageId}' for generation '{generationId:N}'");
    }

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
                throw new InvalidOperationException($"Selected package view '{viewId}' could not be prepared.");
            }

            await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                stagedView!,
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
