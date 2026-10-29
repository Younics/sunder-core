using System.Diagnostics;
using Avalonia.Controls;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Features.Shell.Lifecycle;

internal sealed class ShellPackageViewPresentationCoordinator : IDisposable
{
    private readonly AppPackageLifecycleCoordinator _packageLifecycleCoordinator;
    private readonly ShellPackageLifecyclePresenter _packageLifecyclePresenter;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly IReadOnlyDictionary<string, ShellPackageView> _viewsById;
    private readonly ShellState _shellState;
    private readonly ShellSelectionPresenter _selectionPresenter;
    private readonly Func<RailPlacement, PackageIconBarViewModel> _getBar;
    private readonly Func<RailPlacement, ShellPanelViewModel> _getPanel;
    private readonly Action<RailPlacement, string?, bool> _applyPanelContent;
    private readonly Action _notifyLayoutStateChanged;
    private readonly Action _persistShellState;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly bool _isDeveloperMode;
    private readonly AppPackageLifecycleGate _packageLifecycleGate =
        new(nameof(ShellPackageViewPresentationCoordinator));
    private readonly OwnedTaskObserver _tasks =
        new(nameof(ShellPackageViewPresentationCoordinator));
    private readonly object _requestSyncRoot = new();
    private readonly Dictionary<RailPlacement, long> _requestRevisions = [];
    private readonly Dictionary<RailPlacement, CancellationTokenSource> _requestCancellations = [];
    private readonly Dictionary<RailPlacement, PackageNavigationStage> _navigationStages = [];
    private Action<Control>? _stageCandidateView;
    private Action? _detachStagedCandidateViews;
    private Func<IDisposable?>? _acquirePackageViewTransitionSnapshot;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task> _runPostPresentationWorkAsync =
        static (work, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work(cancellationToken);
        };
    private bool _requestsSuspended;
    private bool _disposed;
    private long _nextRequestRevision;

    public ShellPackageViewPresentationCoordinator(
        AppPackageLifecycleCoordinator packageLifecycleCoordinator,
        ShellPackageLifecyclePresenter packageLifecyclePresenter,
        PackageViewHostService packageViewHostService,
        IReadOnlyDictionary<string, ShellPackageView> viewsById,
        ShellState shellState,
        ShellSelectionPresenter selectionPresenter,
        Func<RailPlacement, PackageIconBarViewModel> getBar,
        Func<RailPlacement, ShellPanelViewModel> getPanel,
        Action<RailPlacement, string?, bool> applyPanelContent,
        Action notifyLayoutStateChanged,
        Action persistShellState,
        IUiDispatcher uiDispatcher,
        bool isDeveloperMode)
    {
        _packageLifecycleCoordinator = packageLifecycleCoordinator;
        _packageLifecyclePresenter = packageLifecyclePresenter;
        _packageViewHostService = packageViewHostService;
        _viewsById = viewsById;
        _shellState = shellState;
        _selectionPresenter = selectionPresenter;
        _getBar = getBar;
        _getPanel = getPanel;
        _applyPanelContent = applyPanelContent;
        _notifyLayoutStateChanged = notifyLayoutStateChanged;
        _persistShellState = persistShellState;
        _uiDispatcher = uiDispatcher;
        _isDeveloperMode = isDeveloperMode;
    }

    public bool CanStartPackageViewInteraction
    {
        get
        {
            lock (_requestSyncRoot)
            {
                return !_disposed && !_requestsSuspended;
            }
        }
    }

    public void ConfigureStagingSurface(
        Action<Control> stageCandidateView,
        Action detachStagedCandidateViews,
        Func<IDisposable?>? acquirePackageViewTransitionSnapshot)
    {
        ArgumentNullException.ThrowIfNull(stageCandidateView);
        ArgumentNullException.ThrowIfNull(detachStagedCandidateViews);
        if (_stageCandidateView is not null)
        {
            throw new InvalidOperationException(
                "The package view staging surface is already configured.");
        }

        _stageCandidateView = stageCandidateView;
        _detachStagedCandidateViews = detachStagedCandidateViews;
        _acquirePackageViewTransitionSnapshot = acquirePackageViewTransitionSnapshot;
    }

    public void ConfigureWorkScheduler(
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runWorkAsync)
    {
        ArgumentNullException.ThrowIfNull(runWorkAsync);
        _runPostPresentationWorkAsync = runWorkAsync;
    }

    public async Task ApplyPackageLifecycleChangesAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        CancellationToken cancellationToken = default,
        Action? detachAuxiliaryPackageViews = null,
        Func<CancellationToken, Task>? refreshAuxiliaryPackageViewsAsync = null)
    {
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _tasks.Token);
        var operationCancellationToken = lifetimeCancellation.Token;
        using var lifecycle = await _packageLifecycleGate
            .EnterAsync(operationCancellationToken)
            .ConfigureAwait(false);
        operationCancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }

        var shellPresentationCommitted = false;
        try
        {
            await _packageLifecycleCoordinator.ApplyPackageSnapshotAsync(
                snapshot,
                packageSources,
                retryDisabledPackageIds,
                async (candidate, activePackages, prepareCancellationToken) =>
                {
                    var presentation = _uiDispatcher.CheckAccess()
                        ? _packageLifecyclePresenter.PrepareLifecycleChanges(
                            activePackages,
                            snapshot.ActivePackages, snapshot.SessionPackages)
                        : await _uiDispatcher.InvokeAsync(
                            () => _packageLifecyclePresenter.PrepareLifecycleChanges(
                                activePackages,
                                snapshot.ActivePackages, snapshot.SessionPackages));
                    var stabilizedViewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var viewId in presentation.SelectedViewIds)
                    {
                        prepareCancellationToken.ThrowIfCancellationRequested();
                        await candidate.StabilizeViewAsync(
                            viewId,
                            parameters: null,
                            StageCandidateView,
                            prepareCancellationToken).ConfigureAwait(false);
                        stabilizedViewIds.Add(viewId);
                    }

                    return () =>
                    {
                        BeginLifecyclePresentationCommit();
                        try
                        {
                            DetachStagedCandidateViews();
                            detachAuxiliaryPackageViews?.Invoke();
                            _packageLifecyclePresenter.CommitPreparedLifecycleChanges(
                                presentation,
                                stabilizedViewIds,
                                candidate.RetainedPackageIds);
                            shellPresentationCommitted = true;
                        }
                        finally
                        {
                            CompleteLifecyclePresentationCommit();
                        }
                    };
                },
                operationCancellationToken,
                async resetCancellationToken =>
                {
                    if (_uiDispatcher.CheckAccess())
                    {
                        DetachStagedCandidateViews();
                    }
                    else
                    {
                        await _uiDispatcher.InvokeAsync(
                            DetachStagedCandidateViews,
                            resetCancellationToken).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            if (_uiDispatcher.CheckAccess())
            {
                DetachStagedCandidateViews();
            }
            else
            {
                await _uiDispatcher.InvokeAsync(DetachStagedCandidateViews).ConfigureAwait(false);
            }
        }

        if (refreshAuxiliaryPackageViewsAsync is null)
        {
            return;
        }

        try
        {
            await refreshAuxiliaryPackageViewsAsync(operationCancellationToken).ConfigureAwait(false);
        }
        // The authoritative shell is already committed; caller cancellation may only skip
        // the optional secondary-window projection.
        catch (OperationCanceledException exception) when (
            shellPresentationCommitted
            && cancellationToken.IsCancellationRequested
            && AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteInfo(
                "Skipped auxiliary package-window refresh after the shell lifecycle commit was applied.");
        }
    }

    public async Task ApplyPackageMetadataAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? refreshAuxiliaryPackageViewsAsync = null)
    {
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _tasks.Token);
        var operationCancellationToken = lifetimeCancellation.Token;
        using var lifecycle = await _packageLifecycleGate
            .EnterAsync(operationCancellationToken)
            .ConfigureAwait(false);
        operationCancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return;
        }
        if (_uiDispatcher.CheckAccess())
        {
            _packageLifecyclePresenter.ApplyPackageMetadata(activePackages);
        }
        else
        {
            await _uiDispatcher.InvokeAsync(
                    () => _packageLifecyclePresenter.ApplyPackageMetadata(activePackages))
                .ConfigureAwait(false);
        }

        if (refreshAuxiliaryPackageViewsAsync is not null)
        {
            await refreshAuxiliaryPackageViewsAsync(operationCancellationToken).ConfigureAwait(false);
        }
    }

    public void ObserveViewNavigation(string viewId)
    {
        if (!_viewsById.ContainsKey(viewId))
        {
            return;
        }

        var request = BeginRequest(viewId);
        _tasks.Observe(
            PresentPackageViewCoreAsync(
                viewId,
                parameters: null,
                request),
            $"presenting package view '{viewId}'");
    }

    public async ValueTask<bool> PresentPackageViewAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters)
        => await PresentPackageViewCoreAsync(
            viewId,
            parameters,
            BeginRequest(viewId));

    public async ValueTask<bool> ReloadPackageViewAsync(
        ShellPackagePanelCoordinator packagePanelCoordinator,
        string viewId)
    {
        if (!_viewsById.ContainsKey(viewId))
        {
            return false;
        }

        var request = BeginRequest(viewId);
        try
        {
            return await packagePanelCoordinator.ReloadPackageViewAsync(
                viewId,
                _runPostPresentationWorkAsync,
                () => IsCurrentRequest(request, viewId),
                () => new ValueTask<bool>(PresentPackageViewCoreAsync(
                    viewId,
                    parameters: null,
                    request)),
                request.CancellationToken);
        }
        catch (OperationCanceledException) when (
            request.CancellationToken.IsCancellationRequested
            && !_tasks.Token.IsCancellationRequested)
        {
            return false;
        }
    }

    public void ObservePanelClose(RailPlacement placement, string viewId)
    {
        var request = BeginRequest(placement);
        var requestedAt = Stopwatch.GetTimestamp();
        if (!IsCurrentRequest(request)
            || ShellSelectionState.GetSelectedViewId(_shellState, placement) is not null)
        {
            return;
        }

        if (_navigationStages.TryGetValue(placement, out var stage))
        {
            ClearNavigationStage(placement, stage, unstageView: true);
        }
        _applyPanelContent(placement, null, false);
        _notifyLayoutStateChanged();
        LogPresentationStage(viewId, "close detach/geometry", requestedAt);
    }

    public void QueueViewNavigationCancellation(string viewId)
    {
        if (!_viewsById.ContainsKey(viewId))
        {
            return;
        }

        var generationId = _packageViewHostService.CurrentGenerationId;
        using (ExecutionContext.SuppressFlow())
        {
            _tasks.Run(
                cancellationToken => _runPostPresentationWorkAsync(
                    async operationCancellation =>
                    {
                        var shouldCancel = await _uiDispatcher.InvokeAsync(
                            () => !IsViewSelected(viewId),
                            operationCancellation);
                        if (!shouldCancel)
                        {
                            return;
                        }

                        await _packageViewHostService.CancelViewNavigationAsync(
                            viewId,
                            generationId,
                            () => !IsViewSelected(viewId));
                    },
                    cancellationToken),
                $"cancelling package view '{viewId}' navigation");
        }
    }

    public void Dispose()
    {
        lock (_requestSyncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _requestsSuspended = true;
        }

        CancelAllRequests();
        ClearAllNavigationStages();
        DetachStagedCandidateViews();
        _packageViewHostService.CancelAllViewNavigations();
        _tasks.Dispose();
    }

    private async Task<bool> PresentPackageViewCoreAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        ViewPresentationRequest request)
    {
        var cancellationToken = request.CancellationToken;
        var requestedAt = Stopwatch.GetTimestamp();
        try
        {
            var usesNavigationPreparation = await _uiDispatcher.InvokeAsync(
                () => IsCurrentRequest(request, viewId)
                    && _packageViewHostService.SupportsNavigationPreparation(
                        viewId,
                        request.GenerationId),
                cancellationToken);
            var acknowledged = await _uiDispatcher.InvokeAsync(
                () => IsCurrentRequest(request, viewId),
                cancellationToken);
            if (!acknowledged)
            {
                return false;
            }

            LogPresentationStage(viewId, "selection acknowledgement", requestedAt);
            LogPresentationStage(viewId, "state/geometry commit", requestedAt);

            if (usesNavigationPreparation)
            {
                var navigationPrepared = false;
                await _runPostPresentationWorkAsync(
                    async operationCancellation =>
                    {
                        if (!IsCurrentRequest(request, viewId))
                        {
                            return;
                        }

                        navigationPrepared = await _packageViewHostService
                            .PrepareNavigationForPresentationAsync(
                                viewId,
                                parameters,
                                request.GenerationId,
                                control => StageNavigationView(request, viewId, control),
                                control => CommitNavigationView(
                                    request,
                                    viewId,
                                    control,
                                    requestedAt),
                                _ => UnstageNavigationView(request),
                                operationCancellation,
                                () => IsCurrentRequest(request, viewId));
                    },
                    cancellationToken);
                if (navigationPrepared)
                {
                    LogPresentationStage(viewId, "prepared navigation", requestedAt);
                    AppSessionLog.WriteInfo(
                        $"Package view '{viewId}' presented in {Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:0.0} ms.",
                        visibleInDeveloperLog: false);
                }
                return navigationPrepared;
            }

            var constructionStartedAt = Stopwatch.GetTimestamp();
            var prepared = false;
            await _runPostPresentationWorkAsync(
                async operationCancellation =>
                {
                    if (!IsCurrentRequest(request, viewId))
                    {
                        return;
                    }

                    prepared = await _packageViewHostService.PrepareViewForPresentationAsync(
                        viewId,
                        request.GenerationId,
                        control =>
                        {
                            if (!IsCurrentRequest(request, viewId)
                                || !_viewsById.TryGetValue(viewId, out var packageView))
                            {
                                return false;
                            }

                            var panel = _getPanel(request.Placement);
                            var retainedBoundary = panel.GetRetainedView(viewId);
                            if (control is not null && retainedBoundary is null)
                            {
                                var boundary = _packageViewHostService.CreateHostedViewBoundary(
                                    packageView.PackageId,
                                    viewId,
                                    control);
                                if (boundary is not null)
                                {
                                    retainedBoundary = panel.RetainHostedView(viewId, boundary);
                                }
                            }

                            if (control is not null && retainedBoundary is null)
                            {
                                return false;
                            }

                            if (!CommitPackageViewState(
                                    request,
                                    viewId,
                                    retainedBoundary,
                                    control is null))
                            {
                                return false;
                            }
                            LogPresentationStage(viewId, "attachment", requestedAt);
                            return control is null
                                || ReferenceEquals(panel.HostedView, retainedBoundary);
                        },
                        operationCancellation);
                },
                cancellationToken);
            LogPresentationStage(viewId, "construction/warmup", constructionStartedAt);
            if (!prepared)
            {
                return false;
            }

            var navigated = false;
            await _runPostPresentationWorkAsync(
                async _ =>
                {
                    if (!IsCurrentRequest(request, viewId))
                    {
                        return;
                    }

                    navigated = await NavigatePresentedViewAsync(
                        viewId,
                        parameters,
                        request,
                        requestedAt);
                },
                cancellationToken);
            return navigated;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            && !_tasks.Token.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool StageNavigationView(
        ViewPresentationRequest request,
        string viewId,
        Control control)
    {
        if (!IsCurrentRequest(request, viewId)
            || !_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var panel = _getPanel(request.Placement);
        if (panel.GetRetainedView(viewId) is null)
        {
            var boundary = _packageViewHostService.CreateHostedViewBoundary(
                packageView.PackageId,
                viewId,
                control);
            if (boundary is not null)
            {
                panel.RetainHostedView(viewId, boundary);
            }
        }

        var snapshotLease = _acquirePackageViewTransitionSnapshot?.Invoke();
        if (_navigationStages.Remove(request.Placement, out var previousStage))
        {
            panel.UnstageHostedView(previousStage.ViewId);
            previousStage.SnapshotLease?.Dispose();
        }

        panel.SetDockStaged(true);
        var staged = panel.StageHostedView(viewId);
        if (staged)
        {
            _navigationStages[request.Placement] = new PackageNavigationStage(
                request.Revision,
                viewId,
                snapshotLease);
            _notifyLayoutStateChanged();
        }
        else
        {
            panel.SetDockStaged(false);
            snapshotLease?.Dispose();
        }
        return staged;
    }

    private bool CommitNavigationView(
        ViewPresentationRequest request,
        string viewId,
        Control control,
        long requestedAt)
    {
        if (!IsCurrentRequest(request, viewId))
        {
            return false;
        }

        var panel = _getPanel(request.Placement);
        var retainedBoundary = panel.GetRetainedView(viewId);
        if (retainedBoundary is null)
        {
            return false;
        }

        if (!CommitPackageViewState(
                request,
                viewId,
                retainedBoundary,
                allowMissingHostedView: false))
        {
            return false;
        }
        CompleteNavigationStage(request, unstageView: false);
        LogPresentationStage(viewId, "prepared attachment", requestedAt);
        return ReferenceEquals(panel.HostedView, retainedBoundary);
    }

    private void UnstageNavigationView(ViewPresentationRequest request)
        => CompleteNavigationStage(request, unstageView: true);

    private bool CommitPackageViewState(
        ViewPresentationRequest request,
        string viewId,
        object? retainedBoundary,
        bool allowMissingHostedView)
    {
        if (!IsCurrentRequest(request, viewId))
        {
            return false;
        }

        if (_navigationStages.TryGetValue(request.Placement, out var staleStage)
            && staleStage.Revision != request.Revision)
        {
            ClearNavigationStage(request.Placement, staleStage, unstageView: true);
        }

        var bar = _getBar(request.Placement);
        var selected = bar.Items.FirstOrDefault(item => string.Equals(
            item.Id,
            viewId,
            StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return false;
        }

        var previousViewId = ShellSelectionState.GetSelectedViewId(
            _shellState,
            request.Placement);
        _selectionPresenter.Select(bar, request.Placement, selected);
        ShellSelectionState.SetSelectedViewId(_shellState, request.Placement, viewId);
        var panel = _getPanel(request.Placement);
        panel.SetDockVisible(true);
        _applyPanelContent(request.Placement, viewId, true);
        _notifyLayoutStateChanged();
        _persistShellState();
        if (!string.IsNullOrWhiteSpace(previousViewId)
            && !string.Equals(previousViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            QueueViewNavigationCancellation(previousViewId);
        }

        return allowMissingHostedView
            || retainedBoundary is not null
            && ReferenceEquals(panel.HostedView, retainedBoundary);
    }

    private void CompleteNavigationStage(
        ViewPresentationRequest request,
        bool unstageView)
    {
        if (!_navigationStages.TryGetValue(request.Placement, out var stage)
            || stage.Revision != request.Revision)
        {
            return;
        }
        ClearNavigationStage(request.Placement, stage, unstageView);
    }

    private void ClearNavigationStage(
        RailPlacement placement,
        PackageNavigationStage stage,
        bool unstageView)
    {
        _navigationStages.Remove(placement);
        var panel = _getPanel(placement);
        if (unstageView)
        {
            panel.UnstageHostedView(stage.ViewId);
        }
        panel.SetDockStaged(false);
        _notifyLayoutStateChanged();
        stage.SnapshotLease?.Dispose();
    }

    private void ClearAllNavigationStages()
    {
        foreach (var stage in _navigationStages.ToArray())
        {
            ClearNavigationStage(stage.Key, stage.Value, unstageView: true);
        }
    }

    private async Task<bool> NavigatePresentedViewAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        ViewPresentationRequest request,
        long requestedAt)
    {
        if (!await NotifyViewNavigatedForPresentationAsync(
                viewId,
                parameters,
                request,
                request.CancellationToken))
        {
            return false;
        }

        LogPresentationStage(viewId, "navigation", requestedAt);
        AppSessionLog.WriteInfo(
            $"Package view '{viewId}' presented in {Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:0.0} ms.",
            visibleInDeveloperLog: false);
        return true;
    }

    private async Task<bool> NotifyViewNavigatedForPresentationAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        ViewPresentationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _packageViewHostService.NotifyViewNavigatedAsync(
                viewId,
                parameters,
                request.GenerationId,
                cancellationToken,
                () => IsCurrentRequest(request, viewId));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private ViewPresentationRequest BeginRequest(string viewId)
        => BeginRequest(_viewsById[viewId].Placement);

    private ViewPresentationRequest BeginRequest(RailPlacement placement)
    {
        CancellationTokenSource? previousCancellation;
        ViewPresentationRequest request;
        lock (_requestSyncRoot)
        {
            if (_disposed || _requestsSuspended)
            {
                return new ViewPresentationRequest(
                    placement,
                    ++_nextRequestRevision,
                    _packageViewHostService.CurrentGenerationId,
                    new CancellationToken(canceled: true));
            }

            _requestCancellations.Remove(placement, out previousCancellation);
            var revision = ++_nextRequestRevision;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token);
            _requestRevisions[placement] = revision;
            _requestCancellations[placement] = cancellation;
            request = new ViewPresentationRequest(
                placement,
                revision,
                _packageViewHostService.CurrentGenerationId,
                cancellation.Token);
        }
        RequestCancellation(
            previousCancellation,
            "superseding package view presentation",
            disposeWhenComplete: true);
        return request;
    }

    private void CancelAllRequests()
    {
        CancellationTokenSource[] cancellations;
        lock (_requestSyncRoot)
        {
            cancellations = _requestCancellations.Values.ToArray();
            _requestCancellations.Clear();
            _requestRevisions.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            RequestCancellation(
                cancellation,
                "cancelling package view presentation",
                disposeWhenComplete: true);
        }
    }

    private void BeginLifecyclePresentationCommit()
    {
        lock (_requestSyncRoot)
        {
            _requestsSuspended = true;
        }
        CancelAllRequests();
        ClearAllNavigationStages();
    }

    private void CompleteLifecyclePresentationCommit()
    {
        lock (_requestSyncRoot)
        {
            _requestsSuspended = false;
        }
    }

    private void RequestCancellation(
        CancellationTokenSource? cancellation,
        string operation,
        bool disposeWhenComplete = false)
    {
        if (cancellation is null)
        {
            return;
        }

        Task cancellationTask;
        try
        {
            cancellationTask = cancellation.CancelAsync();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Package callback failed while {operation}.", ex);
            if (disposeWhenComplete)
            {
                cancellation.Dispose();
            }
            return;
        }

        _tasks.Observe(
            ObserveCancellationAsync(
                cancellationTask,
                cancellation,
                operation,
                disposeWhenComplete),
            operation);
    }

    private static async Task ObserveCancellationAsync(
        Task cancellationTask,
        CancellationTokenSource cancellation,
        string operation,
        bool disposeWhenComplete)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Package callback failed while {operation}.", ex);
        }
        finally
        {
            if (disposeWhenComplete)
            {
                cancellation.Dispose();
            }
        }
    }

    private bool IsCurrentRequest(ViewPresentationRequest request)
    {
        lock (_requestSyncRoot)
        {
            return !_disposed
                && _requestRevisions.TryGetValue(
                    request.Placement,
                    out var currentRevision)
                && currentRevision == request.Revision;
        }
    }

    private bool IsCurrentRequest(
        ViewPresentationRequest request,
        string viewId)
        => IsCurrentRequest(request)
            && request.GenerationId == _packageViewHostService.CurrentGenerationId
            && _viewsById.TryGetValue(viewId, out var packageView)
            && packageView.Placement == request.Placement;

    private bool IsViewSelected(string viewId)
        => ShellSelectionState.GetDeferredActivationSelections(_shellState).Any(selection =>
            string.Equals(selection.ViewId, viewId, StringComparison.OrdinalIgnoreCase));

    private void LogPresentationStage(string viewId, string stage, long startedAt)
    {
        if (!_isDeveloperMode)
        {
            return;
        }

        AppSessionLog.WriteInfo(
            $"Package view '{viewId}' {stage}: {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0} ms.");
    }

    private void StageCandidateView(Control view)
        => (_stageCandidateView
            ?? throw new InvalidOperationException(
                "The package view staging surface is not configured."))(view);

    private void DetachStagedCandidateViews()
        => _detachStagedCandidateViews?.Invoke();

    private readonly record struct ViewPresentationRequest(
        RailPlacement Placement,
        long Revision,
        Guid GenerationId,
        CancellationToken CancellationToken);

    private sealed record PackageNavigationStage(
        long Revision,
        string ViewId,
        IDisposable? SnapshotLease);
}
