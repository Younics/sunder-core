using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Features.Shell.Hotbar;
using Sunder.App.Features.Shell.Items;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Features.Shell.Lifecycle;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int ShellStateSaveDelayMilliseconds = 250;

    private readonly IWindowLauncher _windowLauncher;
    private readonly ShellLayoutStateCoordinator _layoutStateCoordinator;
    private readonly ShellPackageLifecycleRefreshCoordinator _packageLifecycleRefreshCoordinator;
    private readonly ShellPanelContentPresenter _shellPanelContentPresenter;
    private readonly ShellRailCollectionPresenter _railCollectionPresenter;
    private readonly ShellPackagePanelCoordinator _packagePanelCoordinator;
    private readonly ShellDeferredHostedViewActivator _deferredHostedViewActivator;
    private readonly ShellPackageViewPreloader _packageViewPreloader;
    private readonly ShellPackageLifecyclePresenter _packageLifecyclePresenter;
    private readonly ShellHotbarCoordinator _hotbarCoordinator;
    private readonly ShellLayoutPresenter _shellLayout;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly NotificationTrayViewModel _notificationTray;
    private readonly AppUpdatePromptViewModel _appUpdatePrompt;
    private readonly RegistryAuthService? _registryAuthService;
    private readonly ExternalBrowserService? _externalBrowserService;
    private readonly MainWindowSubscriptionScope _subscriptionScope;
    private readonly ShellSelectionPresenter _selectionPresenter = new();
    private readonly ShellItemViewModelFactory _shellItemFactory;
    private readonly ShellState _shellState;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(MainWindowViewModel));
    private readonly Dictionary<string, ShellPackageView> _viewsById;
    private readonly IReadOnlyList<string> _startupWarnings;
    private readonly IReadOnlyList<string> _startupErrors;
    private readonly object _viewPreloadSyncRoot = new();
    private readonly object _viewPresentationSyncRoot = new();
    private readonly Dictionary<RailPlacement, long> _viewPresentationRevisions = [];
    private readonly Dictionary<RailPlacement, CancellationTokenSource> _viewPresentationCancellations = [];
    private readonly Dictionary<RailPlacement, PackageNavigationStage> _packageNavigationStages = [];
    private Action<Control>? _stageCandidateView;
    private Action? _detachStagedCandidateViews;
    private Func<IDisposable?>? _acquirePackageViewTransitionSnapshot;
    private CancellationTokenSource? _viewPreloadCancellation;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task>? _runPreloadWorkAsync;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task> _runPostPresentationWorkAsync =
        static (work, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work(cancellationToken);
        };
    private bool _shellRevealed;
    private bool _shellPersistencePending;
    private bool _viewPresentationRequestsSuspended;
    private bool _disposed;
    private int _viewPreloadSuspensionCount;
    private long _nextViewPresentationRevision;

    internal event Action? ShellViewStateChanged;

    public MainWindowViewModel(
        IWindowLauncher windowLauncher,
        ShellStateService shellStateService,
        ShellSnapshot shellSnapshot,
        PackageViewHostService packageViewHostService,
        RuntimeConnectionState runtimeConnectionState,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        RuntimeHostProcessManager runtimeHostProcessManager,
        SystemStatusResponse? initialSystemStatus,
        NotificationCenterService notificationCenter,
        AppPackageShellViewService? shellViewService = null,
        SunderUpdateService? updateService = null,
        bool deferInitialHostedViews = false,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        AppPackageLifecycleCoordinator? packageLifecycleCoordinator = null,
        IShellCompositionService? shellCompositionService = null,
        DeveloperLogService? developerLog = null,
        RegistryAuthService? registryAuthService = null,
        ExternalBrowserService? externalBrowserService = null,
        IUiDispatcher? uiDispatcher = null
    )
    {
        _windowLauncher = windowLauncher;
        _registryAuthService = registryAuthService;
        _externalBrowserService = externalBrowserService;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _packageViewHostService = packageViewHostService;
        _shellRevealed = !deferInitialHostedViews;
        var effectivePackageLifecycleCoordinator =
            packageLifecycleCoordinator ?? packageViewHostService.LifecycleCoordinator;
        var effectiveShellCompositionService =
            shellCompositionService ?? new ShellCompositionService();
        _appUpdatePrompt = new AppUpdatePromptViewModel(
            new AppUpdatePromptCoordinator(updateService ?? new SunderUpdateService())
        );
        _shellItemFactory = new ShellItemViewModelFactory(packageViewHostService.PackageIconCache);
        _shellState = shellSnapshot.State;
        IsDeveloperMode = developerLog?.IsEnabled == true;
        _layoutStateCoordinator = new ShellLayoutStateCoordinator(
            shellStateService,
            _shellState,
            TimeSpan.FromMilliseconds(ShellStateSaveDelayMilliseconds),
            persistenceEnabled: _shellRevealed
        );
        _viewsById = shellSnapshot.PackageViews.ToDictionary(
            x => x.ViewId,
            StringComparer.OrdinalIgnoreCase
        );
        _startupWarnings = shellSnapshot.StartupWarnings;
        _startupErrors = shellSnapshot.StartupErrors;
        _shellPanelContentPresenter = new ShellPanelContentPresenter(
            packageViewHostService,
            _startupWarnings,
            _startupErrors
        );
        _railCollectionPresenter = new ShellRailCollectionPresenter(
            _viewsById,
            _shellState,
            _selectionPresenter,
            _shellPanelContentPresenter,
            _shellItemFactory.Create,
            ObserveViewNavigation,
            QueueViewNavigationCancellation
        );
        _runtimeStatus = new RuntimeStatusViewModel(
            runtimeConnectionState,
            runtimeApiClientFactory,
            runtimeHostProcessManager,
            shellSnapshot.SystemStatusText,
            initialSystemStatus,
            _startupErrors,
            _layoutStateCoordinator.PersistPreferredRuntimeUrl
        );
        _notificationTray = new NotificationTrayViewModel(notificationCenter, _uiDispatcher);
        BackgroundProcesses = backgroundProcessQueue is null
            ? BackgroundProcessMonitorViewModel.Empty
            : new BackgroundProcessMonitorViewModel(
                backgroundProcessQueue,
                BackgroundProcessIndicator.Main,
                "No visible processes.",
                _shellState.BackgroundProcessPopoverWidth,
                _shellState.BackgroundProcessPopoverHeight,
                _layoutStateCoordinator.PersistBackgroundProcessPopoverSize,
                _uiDispatcher
            );

        _shellLayout = new ShellLayoutPresenter(
            MovePackageView,
            ReloadPackageViewAsync,
            RemovePackageViewFromHotbar,
            ToggleLeftTopView,
            ToggleMiddleView,
            ToggleRightTopView,
            ToggleLeftBottomView,
            ToggleRightBottomView
        );
        _packageViewPreloader = new ShellPackageViewPreloader(
            _viewsById,
            _shellState,
            packageViewHostService,
            () => _shellLayout.GetSlots(),
            _uiDispatcher,
            IsDeveloperMode);
        _hotbarCoordinator = new ShellHotbarCoordinator(
            _viewsById,
            _shellState,
            GetOrderedViewIds,
            OpenPackageViewPanelAsync,
            RebuildRailCollections,
            UpdateRailCollections,
            PersistShellState
        );
        _packagePanelCoordinator = new ShellPackagePanelCoordinator(
            _viewsById,
            _shellState,
            packageViewHostService,
            _selectionPresenter,
            GetBar,
            GetPanel,
            ApplyPanelContent,
            _hotbarCoordinator.IsViewInHotbar,
            _hotbarCoordinator.AddViewToDefaultHotbarAsync,
            ObserveViewNavigation,
            RebuildRailCollections,
            NotifyLayoutStateChanged,
            PersistShellState,
            PresentPackageViewAsync,
            QueueViewNavigationCancellation,
            ObservePanelClose
        );
        _deferredHostedViewActivator = new ShellDeferredHostedViewActivator(
            _shellState,
            () => _disposed,
            PrepareInitialHostedViewAsync,
            NotifyViewNavigatedIgnoringCancellationAsync,
            NotifyLayoutStateChanged
        );
        _packageLifecyclePresenter = new ShellPackageLifecyclePresenter(
            effectiveShellCompositionService,
            _viewsById,
            _shellState,
            _startupWarnings,
            _startupErrors,
            status => SyncStatusText = status,
            RebuildRailCollectionsForLifecycle,
            PersistShellState,
            RemoveRetainedPackageViews
        );
        _packageLifecycleRefreshCoordinator = new ShellPackageLifecycleRefreshCoordinator(
            effectivePackageLifecycleCoordinator,
            _packageLifecyclePresenter,
            () => _disposed,
            StageCandidateView,
            DetachStagedCandidateViews,
            _uiDispatcher,
            BeginPackageLifecyclePresentationCommit,
            CompletePackageLifecyclePresentationCommit
        );
        _subscriptionScope = new MainWindowSubscriptionScope(
            this,
            _windowLauncher,
            _runtimeStatus,
            _notificationTray,
            _appUpdatePrompt,
            packageViewHostService,
            notificationCenter,
            shellViewService,
            BackgroundProcesses,
            _layoutStateCoordinator,
            RuntimeStatus_OnPropertyChanged,
            NotificationTray_OnPropertyChanged,
            AppUpdatePrompt_OnPropertyChanged,
            OnPackageFaulted,
            OnNotificationsChanged,
            OnToastQueued
        );

        LeftPanelWidth = _shellState.LeftPanelWidth;
        RightPanelWidth = _shellState.RightPanelWidth;
        TopRowHeightRatio = _shellState.TopRowHeightRatio;
        BottomSplitRatio = _shellState.BottomSplitRatio;
        SyncStatusText = shellSnapshot.SyncStatusText;
        _notificationTray.ReloadNotifications();

        RebuildRailCollections(createHostedViews: !deferInitialHostedViews);
        PersistShellState();
        _tasks.Observe(RefreshRegistryAccountAsync(), "refreshing the Registry account");
    }

    public PackageIconBarViewModel LeftTopBar => _shellLayout.LeftTopBar;

    public PackageIconBarViewModel MiddleBar => _shellLayout.MiddleBar;

    public PackageIconBarViewModel RightTopBar => _shellLayout.RightTopBar;

    public PackageIconBarViewModel LeftBottomBar => _shellLayout.LeftBottomBar;

    public PackageIconBarViewModel RightBottomBar => _shellLayout.RightBottomBar;

    public BackgroundProcessMonitorViewModel BackgroundProcesses { get; }

    public bool IsDeveloperMode { get; }

    public ShellPanelViewModel LeftTopPanel => _shellLayout.LeftTopPanel;

    public ShellPanelViewModel MiddlePanel => _shellLayout.MiddlePanel;

    public ShellPanelViewModel RightTopPanel => _shellLayout.RightTopPanel;

    public ShellPanelViewModel LeftBottomPanel => _shellLayout.LeftBottomPanel;

    public ShellPanelViewModel RightBottomPanel => _shellLayout.RightBottomPanel;

    public bool HasLeftTopPanelContent => _selectionPresenter.HasLeftTopPanelContent;

    public bool HasMiddleSelection => _selectionPresenter.HasMiddleSelection;

    public bool HasRightTopPanelContent => _selectionPresenter.HasRightTopPanelContent;

    public bool HasLeftBottomPanelContent => _selectionPresenter.HasLeftBottomPanelContent;

    public bool HasRightBottomPanelContent => _selectionPresenter.HasRightBottomPanelContent;

    public bool HasAnyBottomPanelContent => HasLeftBottomPanelContent || HasRightBottomPanelContent;

    public bool HasBottomSplitPanelContent =>
        HasLeftBottomPanelContent && HasRightBottomPanelContent;

    [ObservableProperty]
    private double _leftPanelWidth = ShellState.DefaultLeftPanelWidth;

    [ObservableProperty]
    private double _rightPanelWidth = ShellState.DefaultRightPanelWidth;

    [ObservableProperty]
    private double _topRowHeightRatio = ShellState.DefaultTopRowHeightRatio;

    [ObservableProperty]
    private double _bottomSplitRatio = ShellState.DefaultBottomSplitRatio;

    [ObservableProperty]
    private string _syncStatusText = "Synced";

    [RelayCommand]
    private void OpenPackages() => _windowLauncher.ShowPackages();

    [RelayCommand]
    private void OpenStacks() => _windowLauncher.ShowStacks();

    [RelayCommand]
    private void OpenDeveloperLogs() => _windowLauncher.ShowDeveloperLogs();

    [RelayCommand]
    private void OpenSettings() => _windowLauncher.ShowSettings();

    public void AdjustLiveLeftPanelWidth(double delta, double maximumWidth)
    {
        LeftPanelWidth = _layoutStateCoordinator.AdjustLeftPanelWidth(
            LeftPanelWidth,
            delta,
            maximumWidth
        );
    }

    public void AdjustLiveRightPanelWidth(double delta, double maximumWidth)
    {
        RightPanelWidth = _layoutStateCoordinator.AdjustRightPanelWidth(
            RightPanelWidth,
            delta,
            maximumWidth
        );
    }

    public void AdjustLiveTopRowHeightRatio(double deltaRatio)
    {
        TopRowHeightRatio = _layoutStateCoordinator.AdjustTopRowHeightRatio(
            TopRowHeightRatio,
            deltaRatio
        );
    }

    public void AdjustLiveBottomSplitRatio(double deltaRatio)
    {
        BottomSplitRatio = _layoutStateCoordinator.AdjustBottomSplitRatio(
            BottomSplitRatio,
            deltaRatio
        );
    }

    public void CommitLayoutState() => PersistShellState();

    private void RunOnUiThread(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _tasks.Observe(_uiDispatcher.InvokeAsync(action), "updating shell presentation");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPackageViewPreloading();
        CancelAllViewPresentationRequests();
        ClearAllPackageNavigationStages();
        DetachStagedCandidateViews();
        _packageViewHostService.CancelAllViewNavigations();
        _registryAuthRequest.Dispose();
        _appUpdatePrompt.Dispose();
        _tasks.Dispose();
        _hotbarCoordinator.Dispose();
        _subscriptionScope.Dispose();
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<ShellMenuItem> GetMainMenuItems() =>
        ShellMenuProjector.Project(
            _viewsById.Values,
            _shellItemFactory.GetPackageIcon,
            _hotbarCoordinator.IsViewInHotbar,
            async (viewId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await OpenPackageViewPanelAsync(viewId);
            },
            IsDeveloperMode,
            _ =>
            {
                _windowLauncher.ShowDeveloperLogs();
                return Task.CompletedTask;
            }
        );

    internal async Task ApplyPackageLifecycleSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        CancellationToken cancellationToken = default,
        Action? detachAuxiliaryPackageViews = null
    )
    {
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _tasks.Token
        );
        SuspendPackageViewPreloading();
        try
        {
            await _packageLifecycleRefreshCoordinator.ApplyPackageLifecycleChangesAsync(
                snapshot,
                retryDisabledPackageIds,
                lifetimeCancellation.Token,
                detachAuxiliaryPackageViews
            );
        }
        finally
        {
            ResumePackageViewPreloading();
        }
    }

    internal void ConfigurePackageViewStagingSurface(
        Action<Control> stageCandidateView,
        Action detachStagedCandidateViews,
        Func<IDisposable?>? acquirePackageViewTransitionSnapshot = null
    )
    {
        ArgumentNullException.ThrowIfNull(stageCandidateView);
        ArgumentNullException.ThrowIfNull(detachStagedCandidateViews);
        if (_stageCandidateView is not null)
        {
            throw new InvalidOperationException(
                "The package view staging surface is already configured."
            );
        }

        _stageCandidateView = stageCandidateView;
        _detachStagedCandidateViews = detachStagedCandidateViews;
        _acquirePackageViewTransitionSnapshot = acquirePackageViewTransitionSnapshot;
    }

    public async Task ActivateDeferredInitialHostedViewsAsync(
        Func<Task>? waitForAttachmentAsync = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_disposed)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _tasks.Token
        );
        await _deferredHostedViewActivator.ActivateInitialHostedViewsAsync(
            waitForAttachmentAsync,
            lifetimeCancellation.Token
        );
    }

    public void CompleteInitialReveal()
    {
        if (_shellRevealed)
        {
            return;
        }

        _shellRevealed = true;
        _layoutStateCoordinator.EnablePersistence();
        if (_shellPersistencePending)
        {
            _shellPersistencePending = false;
        }
        PersistShellState();
    }

    internal void StartPackageViewPreloading(
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runPreloadWorkAsync,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runPostPresentationWorkAsync)
    {
        ArgumentNullException.ThrowIfNull(runPreloadWorkAsync);
        ArgumentNullException.ThrowIfNull(runPostPresentationWorkAsync);
        _runPreloadWorkAsync = runPreloadWorkAsync;
        _runPostPresentationWorkAsync = runPostPresentationWorkAsync;
        RestartPackageViewPreloading();
    }

    internal void ConfigurePackageViewWorkScheduler(
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runWorkAsync)
    {
        ArgumentNullException.ThrowIfNull(runWorkAsync);
        _runPostPresentationWorkAsync = runWorkAsync;
    }

    private void RestartPackageViewPreloading()
    {
        if (!_shellRevealed || _runPreloadWorkAsync is null)
        {
            return;
        }

        CancellationTokenSource? previousCancellation;
        CancellationTokenSource preloadCancellation;
        lock (_viewPreloadSyncRoot)
        {
            if (_disposed || _viewPreloadSuspensionCount > 0)
            {
                return;
            }

            previousCancellation = _viewPreloadCancellation;
            preloadCancellation = new CancellationTokenSource();
            _viewPreloadCancellation = preloadCancellation;
        }
        RequestCancellation(previousCancellation, "replacing package view preloading");
        var preloadTask = RunPackageViewPreloadingAsync(
            preloadCancellation,
            _tasks.Token);
        _tasks.Observe(preloadTask, "preloading package views");
    }

    private async Task RunPackageViewPreloadingAsync(
        CancellationTokenSource preloadCancellation,
        CancellationToken ownerCancellation)
    {
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                preloadCancellation.Token,
                ownerCancellation);
            await _packageViewPreloader.PreloadAsync(
                _runPreloadWorkAsync!,
                linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (preloadCancellation.IsCancellationRequested)
        {
            // Lifecycle changes and explicit reloads routinely replace the preload pass.
        }
        finally
        {
            lock (_viewPreloadSyncRoot)
            {
                if (ReferenceEquals(_viewPreloadCancellation, preloadCancellation))
                {
                    _viewPreloadCancellation = null;
                }
            }
            preloadCancellation.Dispose();
        }
    }

    private void CancelPackageViewPreloading()
    {
        CancellationTokenSource? cancellation;
        lock (_viewPreloadSyncRoot)
        {
            cancellation = _viewPreloadCancellation;
        }
        RequestCancellation(cancellation, "cancelling package view preloading");
    }

    private void SuspendPackageViewPreloading()
    {
        CancellationTokenSource? cancellation;
        lock (_viewPreloadSyncRoot)
        {
            _viewPreloadSuspensionCount++;
            cancellation = _viewPreloadCancellation;
        }
        RequestCancellation(cancellation, "suspending package view preloading");
    }

    private void ResumePackageViewPreloading()
    {
        bool restart;
        lock (_viewPreloadSyncRoot)
        {
            if (_viewPreloadSuspensionCount <= 0)
            {
                throw new InvalidOperationException("Package view preloading is not suspended.");
            }

            _viewPreloadSuspensionCount--;
            restart = _viewPreloadSuspensionCount == 0 && !_disposed;
        }

        if (restart)
        {
            RestartPackageViewPreloading();
        }
    }

    internal IReadOnlyList<(string ViewId, Control View)> GetActiveHostedViewControls() =>
        _shellLayout
            .GetSlots()
            .Select(slot => (slot.Panel.ActiveViewId, slot.Panel.HostedView))
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ActiveViewId) && item.HostedView is Control
            )
            .Select(item => (item.ActiveViewId!, (Control)item.HostedView!))
            .ToArray();

    public void ActivatePackageView(string viewId) =>
        _tasks.Observe(OpenPackageViewPanelAsync(viewId).AsTask(), "opening a package view");

    public IReadOnlyList<PackageHotbarView> ListHotbarViews() =>
        PackageHotbarProjector.Project(
            GetOrderedViewsForPlacement,
            placement => ShellSelectionState.GetSelectedViewId(_shellState, placement)
        );

    public bool IsViewInHotbar(string viewId) => _hotbarCoordinator.IsViewInHotbar(viewId);

    public async ValueTask<bool> AddPackageViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null
    )
        => await _hotbarCoordinator.AddViewToDefaultHotbarAsync(
            viewId,
            openPanel,
            parameters);

    public async ValueTask<bool> AddPackageViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null
    )
        => await _hotbarCoordinator.AddViewToHotbarAsync(
            viewId,
            placement,
            index,
            openPanel,
            parameters);

    public bool RemovePackageViewFromHotbar(string viewId)
    {
        var placement = _viewsById.TryGetValue(viewId, out var packageView)
            ? packageView.Placement
            : (RailPlacement?)null;
        var removed = _hotbarCoordinator.RemoveViewFromHotbar(viewId);
        if (removed)
        {
            if (placement is not null)
            {
                var selectedViewId = ShellSelectionState.GetSelectedViewId(
                    _shellState,
                    placement.Value);
                if (!string.IsNullOrWhiteSpace(selectedViewId))
                {
                    ObserveViewNavigation(selectedViewId);
                }
            }
        }
        return removed;
    }

    public async ValueTask<bool> ReloadPackageViewAsync(string viewId)
    {
        if (!_viewsById.ContainsKey(viewId))
        {
            return false;
        }

        SuspendPackageViewPreloading();
        var request = BeginViewPresentationRequest(viewId);
        try
        {
            return await _packagePanelCoordinator.ReloadPackageViewAsync(
                viewId,
                _runPostPresentationWorkAsync,
                () => IsCurrentViewPresentationRequest(request, viewId),
                () => new ValueTask<bool>(PresentPackageViewCoreAsync(
                    viewId,
                    parameters: null,
                    request)),
                request.CancellationToken);
        }
        catch (OperationCanceledException) when (
            request.CancellationToken.IsCancellationRequested && !_tasks.Token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            ResumePackageViewPreloading();
        }
    }

    public async ValueTask<bool> OpenPackageViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null
    )
    {
        if (!CanStartPackageViewInteraction())
        {
            return false;
        }

        return await _packagePanelCoordinator.OpenPackageViewPanelAsync(viewId, parameters);
    }

    public bool ClosePackageViewPanel(string viewId)
    {
        if (!CanStartPackageViewInteraction())
        {
            return false;
        }

        return _packagePanelCoordinator.ClosePackageViewPanel(viewId);
    }

    public void MovePackageView(string viewId, RailPlacement placement, int? targetIndex) =>
        _hotbarCoordinator.MoveView(viewId, placement, targetIndex);

    private void RebuildRailCollections(bool createHostedViews = true) =>
        RebuildRailCollectionsForLifecycle(createHostedViews, null);

    private void RebuildRailCollectionsForLifecycle(
        bool createHostedViews,
        IReadOnlySet<string>? stabilizedViewIds
    )
    {
        _railCollectionPresenter.Rebuild(
            _shellLayout.GetSlots(),
            createHostedViews,
            stabilizedViewIds
        );
        _packageViewPreloader.RetainHotbarViews();
        NotifyLayoutStateChanged();
    }

    private void UpdateRailCollections(
        IReadOnlySet<RailPlacement> placements,
        IReadOnlySet<string> impactedPackageIds,
        bool createHostedViews
    )
    {
        _railCollectionPresenter.Update(
            _shellLayout.GetSlots(),
            placements,
            impactedPackageIds,
            createHostedViews
        );
        _packageViewPreloader.RetainHotbarViews();
        NotifyLayoutStateChanged();
    }

    private IEnumerable<ShellPackageView> GetOrderedViewsForPlacement(RailPlacement placement) =>
        ShellViewOrdering.GetOrderedViewsForPlacement(_viewsById.Values, _shellState, placement);

    private List<string> GetOrderedViewIds(RailPlacement placement)
    {
        return GetOrderedViewsForPlacement(placement).Select(view => view.ViewId).ToList();
    }

    private void ToggleLeftTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleMiddleView(ShellItemViewModel item) => SelectItem(item, allowToggle: false);

    private void ToggleRightTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleLeftBottomView(ShellItemViewModel item) =>
        SelectItem(item, allowToggle: true);

    private void ToggleRightBottomView(ShellItemViewModel item) =>
        SelectItem(item, allowToggle: true);

    private void SelectItem(ShellItemViewModel item, bool allowToggle)
    {
        if (CanStartPackageViewInteraction())
        {
            _packagePanelCoordinator.SelectItem(item, allowToggle);
        }
    }

    private void ObserveViewNavigation(string viewId)
    {
        if (!_viewsById.ContainsKey(viewId))
        {
            return;
        }

        var request = BeginViewPresentationRequest(viewId);
        _tasks.Observe(
            PresentPackageViewCoreAsync(
                viewId,
                parameters: null,
                request),
            $"presenting package view '{viewId}'"
        );
    }

    private async ValueTask<bool> PresentPackageViewAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters)
        => await PresentPackageViewCoreAsync(
            viewId,
            parameters,
            BeginViewPresentationRequest(viewId));

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
                () => IsCurrentViewPresentationRequest(request, viewId)
                    && _packageViewHostService.SupportsNavigationPreparation(
                        viewId,
                        request.GenerationId),
                cancellationToken);
            var acknowledged = await _uiDispatcher.InvokeAsync(
                () =>
                {
                    return IsCurrentViewPresentationRequest(request, viewId);
                },
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
                        if (!IsCurrentViewPresentationRequest(request, viewId))
                        {
                            return;
                        }

                        navigationPrepared = await _packageViewHostService
                            .PrepareNavigationForPresentationAsync(
                                viewId,
                                parameters,
                                request.GenerationId,
                                control => StagePackageNavigationView(
                                    request,
                                    viewId,
                                    control),
                                control => CommitPackageNavigationView(
                                    request,
                                    viewId,
                                    control,
                                    requestedAt),
                                _ => UnstagePackageNavigationView(request),
                                operationCancellation,
                                () => IsCurrentViewPresentationRequest(request, viewId));
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
                    if (!IsCurrentViewPresentationRequest(request, viewId))
                    {
                        return;
                    }

                    prepared = await _packageViewHostService.PrepareViewForPresentationAsync(
                        viewId,
                        request.GenerationId,
                        control =>
                        {
                            if (!IsCurrentViewPresentationRequest(request, viewId)
                                || !_viewsById.TryGetValue(viewId, out var packageView))
                            {
                                return false;
                            }

                            var panel = GetPanel(request.Placement);
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
                    if (!IsCurrentViewPresentationRequest(request, viewId))
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
            cancellationToken.IsCancellationRequested && !_tasks.Token.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool StagePackageNavigationView(
        ViewPresentationRequest request,
        string viewId,
        Control control)
    {
        if (!IsCurrentViewPresentationRequest(request, viewId)
            || !_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var panel = GetPanel(request.Placement);
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
        if (_packageNavigationStages.Remove(request.Placement, out var previousStage))
        {
            panel.UnstageHostedView(previousStage.ViewId);
            previousStage.SnapshotLease?.Dispose();
        }

        panel.SetDockStaged(true);
        var staged = panel.StageHostedView(viewId);
        if (staged)
        {
            _packageNavigationStages[request.Placement] = new PackageNavigationStage(
                request.Revision,
                viewId,
                snapshotLease);
            NotifyLayoutStateChanged();
        }
        else
        {
            panel.SetDockStaged(false);
            snapshotLease?.Dispose();
        }
        return staged;
    }

    private bool CommitPackageNavigationView(
        ViewPresentationRequest request,
        string viewId,
        Control control,
        long requestedAt)
    {
        if (!IsCurrentViewPresentationRequest(request, viewId))
        {
            return false;
        }

        var panel = GetPanel(request.Placement);
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
        CompletePackageNavigationStage(request, unstageView: false);
        LogPresentationStage(viewId, "prepared attachment", requestedAt);
        return ReferenceEquals(panel.HostedView, retainedBoundary);
    }

    private void UnstagePackageNavigationView(ViewPresentationRequest request)
    {
        CompletePackageNavigationStage(request, unstageView: true);
    }

    private bool CommitPackageViewState(
        ViewPresentationRequest request,
        string viewId,
        object? retainedBoundary,
        bool allowMissingHostedView)
    {
        if (!IsCurrentViewPresentationRequest(request, viewId))
        {
            return false;
        }

        if (_packageNavigationStages.TryGetValue(request.Placement, out var staleStage)
            && staleStage.Revision != request.Revision)
        {
            ClearPackageNavigationStage(request.Placement, staleStage, unstageView: true);
        }

        var bar = GetBar(request.Placement);
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
        var panel = GetPanel(request.Placement);
        panel.SetDockVisible(true);
        ApplyPanelContent(request.Placement, viewId, createHostedView: true);
        NotifyLayoutStateChanged();
        PersistShellState();
        if (!string.IsNullOrWhiteSpace(previousViewId)
            && !string.Equals(previousViewId, viewId, StringComparison.OrdinalIgnoreCase))
        {
            QueueViewNavigationCancellation(previousViewId);
        }

        return allowMissingHostedView
            || retainedBoundary is not null
            && ReferenceEquals(panel.HostedView, retainedBoundary);
    }

    private void CompletePackageNavigationStage(
        ViewPresentationRequest request,
        bool unstageView)
    {
        if (!_packageNavigationStages.TryGetValue(request.Placement, out var stage)
            || stage.Revision != request.Revision)
        {
            return;
        }
        ClearPackageNavigationStage(request.Placement, stage, unstageView);
    }

    private void ClearPackageNavigationStage(
        RailPlacement placement,
        PackageNavigationStage stage,
        bool unstageView)
    {
        _packageNavigationStages.Remove(placement);
        var panel = GetPanel(placement);
        if (unstageView)
        {
            panel.UnstageHostedView(stage.ViewId);
        }
        panel.SetDockStaged(false);
        NotifyLayoutStateChanged();
        stage.SnapshotLease?.Dispose();
    }

    private void ClearAllPackageNavigationStages()
    {
        foreach (var stage in _packageNavigationStages.ToArray())
        {
            ClearPackageNavigationStage(stage.Key, stage.Value, unstageView: true);
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

    private void ObservePanelClose(RailPlacement placement, string viewId)
    {
        var request = BeginViewPresentationRequest(placement);
        var requestedAt = Stopwatch.GetTimestamp();
        if (!IsCurrentViewPresentationRequest(request)
            || ShellSelectionState.GetSelectedViewId(_shellState, placement) is not null)
        {
            return;
        }

        if (_packageNavigationStages.TryGetValue(placement, out var stage))
        {
            ClearPackageNavigationStage(placement, stage, unstageView: true);
        }
        ApplyPanelContent(placement, viewId: null, createHostedView: false);
        NotifyLayoutStateChanged();
        LogPresentationStage(viewId, "close detach/geometry", requestedAt);
    }

    private void QueueViewNavigationCancellation(string viewId)
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

    private async Task NotifyViewNavigatedIgnoringCancellationAsync(
        string viewId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _packageViewHostService.NotifyViewNavigatedAsync(
                viewId,
                parameters: null,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseding navigation and panel closure cancel the previous presentation.
        }
    }

    private async Task<bool> PrepareInitialHostedViewAsync(
        RailPlacement placement,
        string viewId,
        CancellationToken cancellationToken)
    {
        var generationId = _packageViewHostService.CurrentGenerationId;
        return await _packageViewHostService.PrepareViewForPresentationAsync(
            viewId,
            generationId,
            control =>
            {
                if (_disposed
                    || control is null
                    || _packageViewHostService.CurrentGenerationId != generationId
                    || !_viewsById.TryGetValue(viewId, out var packageView)
                    || packageView.Placement != placement
                    || !string.Equals(
                        ShellSelectionState.GetSelectedViewId(_shellState, placement),
                        viewId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var panel = GetPanel(placement);
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

                ApplyPanelContent(placement, viewId, createHostedView: true);
                return panel.HostedView is not null;
            },
            cancellationToken).ConfigureAwait(false);
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
                () => IsCurrentViewPresentationRequest(request, viewId));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private ViewPresentationRequest BeginViewPresentationRequest(string viewId)
        => BeginViewPresentationRequest(_viewsById[viewId].Placement);

    private ViewPresentationRequest BeginViewPresentationRequest(RailPlacement placement)
    {
        CancellationTokenSource? previousCancellation;
        ViewPresentationRequest request;
        lock (_viewPresentationSyncRoot)
        {
            if (_disposed || _viewPresentationRequestsSuspended)
            {
                return new ViewPresentationRequest(
                    placement,
                    ++_nextViewPresentationRevision,
                    _packageViewHostService.CurrentGenerationId,
                    new CancellationToken(canceled: true));
            }

            _viewPresentationCancellations.Remove(placement, out previousCancellation);
            var revision = ++_nextViewPresentationRevision;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_tasks.Token);
            _viewPresentationRevisions[placement] = revision;
            _viewPresentationCancellations[placement] = cancellation;
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

    private void CancelAllViewPresentationRequests()
    {
        CancellationTokenSource[] cancellations;
        lock (_viewPresentationSyncRoot)
        {
            cancellations = _viewPresentationCancellations.Values.ToArray();
            _viewPresentationCancellations.Clear();
            _viewPresentationRevisions.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            RequestCancellation(
                cancellation,
                "cancelling package view presentation",
                disposeWhenComplete: true);
        }
    }

    private bool CanStartPackageViewInteraction()
    {
        lock (_viewPresentationSyncRoot)
        {
            return !_disposed && !_viewPresentationRequestsSuspended;
        }
    }

    private void BeginPackageLifecyclePresentationCommit()
    {
        lock (_viewPresentationSyncRoot)
        {
            _viewPresentationRequestsSuspended = true;
        }
        CancelAllViewPresentationRequests();
        ClearAllPackageNavigationStages();
    }

    private void CompletePackageLifecyclePresentationCommit()
    {
        lock (_viewPresentationSyncRoot)
        {
            _viewPresentationRequestsSuspended = false;
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

    private bool IsCurrentViewPresentationRequest(ViewPresentationRequest request)
    {
        lock (_viewPresentationSyncRoot)
        {
            return !_disposed
                && _viewPresentationRevisions.TryGetValue(
                    request.Placement,
                    out var currentRevision)
                && currentRevision == request.Revision;
        }
    }

    private bool IsCurrentViewPresentationRequest(
        ViewPresentationRequest request,
        string viewId)
        => IsCurrentViewPresentationRequest(request)
            && request.GenerationId == _packageViewHostService.CurrentGenerationId
            && _viewsById.TryGetValue(viewId, out var packageView)
            && packageView.Placement == request.Placement;

    private bool IsViewSelected(string viewId)
        => _shellLayout.GetSlots().Any(slot => string.Equals(
            ShellSelectionState.GetSelectedViewId(_shellState, slot.Placement),
            viewId,
            StringComparison.OrdinalIgnoreCase));

    private void LogPresentationStage(string viewId, string stage, long startedAt)
    {
        if (!IsDeveloperMode)
        {
            return;
        }

        AppSessionLog.WriteInfo(
            $"Package view '{viewId}' {stage}: {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0} ms.");
    }

    private void ApplyPanelContent(
        RailPlacement placement,
        string? viewId,
        bool createHostedView = true
    )
    {
        var panel = GetPanel(placement);
        _shellPanelContentPresenter.Apply(
            panel,
            placement,
            viewId,
            _viewsById,
            MiddleBar.Items.Count,
            createHostedView
        );
    }

    private void OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _tasks.Observe(
                _uiDispatcher.InvokeAsync(() => OnPackageFaulted(sender, e)),
                "presenting a package fault"
            );
            return;
        }

        SuspendPackageViewPreloading();
        try
        {
            var affectedPlacements = _viewsById
                .Values.Where(view =>
                    string.Equals(view.PackageId, e.PackageId, StringComparison.OrdinalIgnoreCase)
                )
                .Select(view => view.Placement)
                .ToHashSet();
            RemoveRetainedPackageViews(
                new HashSet<string>([e.PackageId], StringComparer.OrdinalIgnoreCase)
            );
            if (!_packageLifecyclePresenter.RemovePackageViewsFromShell(e.PackageId))
            {
                return;
            }

            UpdateRailCollections(
                affectedPlacements,
                new HashSet<string>([e.PackageId], StringComparer.OrdinalIgnoreCase),
                createHostedViews: true
            );
            PersistShellState();
        }
        finally
        {
            ResumePackageViewPreloading();
        }
    }

    private readonly record struct ViewPresentationRequest(
        RailPlacement Placement,
        long Revision,
        Guid GenerationId,
        CancellationToken CancellationToken);

    private sealed record PackageNavigationStage(
        long Revision,
        string ViewId,
        IDisposable? SnapshotLease);

    private void PersistShellState()
    {
        if (!_shellRevealed)
        {
            _shellPersistencePending = true;
            return;
        }

        _layoutStateCoordinator.PersistShellLayout(
            LeftPanelWidth,
            RightPanelWidth,
            TopRowHeightRatio,
            BottomSplitRatio
        );
    }

    private void StageCandidateView(Control view) =>
        (
            _stageCandidateView
            ?? throw new InvalidOperationException(
                "The package view staging surface is not configured."
            )
        )(view);

    private void DetachStagedCandidateViews() => _detachStagedCandidateViews?.Invoke();

    private PackageIconBarViewModel GetBar(RailPlacement placement) =>
        _shellLayout.GetBar(placement);

    private ShellPanelViewModel GetPanel(RailPlacement placement) =>
        _shellLayout.GetPanel(placement);

    private void RemoveRetainedPackageViews(IReadOnlySet<string> packageIds)
    {
        if (packageIds.Count == 0)
        {
            return;
        }

        foreach (var slot in _shellLayout.GetSlots())
        {
            foreach (var retainedView in slot.Panel.HostedViews.ToArray())
            {
                if (
                    _viewsById.TryGetValue(retainedView.ViewId, out var packageView)
                    && packageIds.Contains(packageView.PackageId)
                )
                {
                    slot.Panel.RemoveHostedView(retainedView.ViewId);
                }
            }
        }
    }

    private void NotifyLayoutStateChanged()
    {
        ShellViewStateChanged?.Invoke();
    }
}
