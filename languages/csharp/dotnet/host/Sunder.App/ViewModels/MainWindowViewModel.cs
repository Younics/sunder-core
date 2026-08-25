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
    private readonly ShellPackageViewPresentationCoordinator _packageViewPresentationCoordinator;
    private readonly ShellPanelContentPresenter _shellPanelContentPresenter;
    private readonly ShellRailCollectionPresenter _railCollectionPresenter;
    private readonly ShellPackagePanelCoordinator _packagePanelCoordinator;
    private readonly ShellDeferredHostedViewActivator _deferredHostedViewActivator;
    private readonly ShellPackageViewPreloader _packageViewPreloader;
    private readonly ShellHotbarCoordinator _hotbarCoordinator;
    private readonly ShellLayoutPresenter _shellLayout;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly NotificationTrayViewModel _notificationTray;
    private readonly AppUpdatePromptViewModel _appUpdatePrompt;
    private readonly RegistryAuthService? _registryAuthService;
    private readonly ExternalBrowserService? _externalBrowserService;
    private readonly RemoteImageService? _remoteImageService;
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
    private CancellationTokenSource? _viewPreloadCancellation;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task>? _runPreloadWorkAsync;
    private bool _shellRevealed;
    private bool _shellPersistencePending;
    private bool _disposed;
    private int _viewPreloadSuspensionCount;

    internal event Action? ShellViewStateChanged;

    public MainWindowViewModel(
        IWindowLauncher windowLauncher,
        ShellStateService shellStateService,
        ShellSnapshot shellSnapshot,
        PackageViewHostService packageViewHostService,
        RuntimeConnectionState runtimeConnectionState,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        RuntimeAvailabilityManager runtimeAvailabilityManager,
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
        RemoteImageService? remoteImageService = null,
        IUiDispatcher? uiDispatcher = null
    )
    {
        _windowLauncher = windowLauncher;
        _registryAuthService = registryAuthService;
        _externalBrowserService = externalBrowserService;
        _remoteImageService = remoteImageService;
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
            runtimeAvailabilityManager,
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
        var packageLifecyclePresenter = new ShellPackageLifecyclePresenter(
            effectiveShellCompositionService,
            _viewsById,
            _shellState,
            _startupWarnings,
            _startupErrors,
            status => SyncStatusText = status,
            RebuildRailCollectionsForLifecycle,
            PersistShellState,
            RemoveRetainedPackageViews,
            RefreshPackageMetadata,
            packageViewHostService.IsPackageAppUiQuarantined
        );
        _packageViewPresentationCoordinator = new ShellPackageViewPresentationCoordinator(
            effectivePackageLifecycleCoordinator,
            packageLifecyclePresenter,
            packageViewHostService,
            _viewsById,
            _shellState,
            _selectionPresenter,
            GetBar,
            GetPanel,
            ApplyPanelContent,
            NotifyLayoutStateChanged,
            PersistShellState,
            _uiDispatcher,
            IsDeveloperMode
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
        _packageViewPresentationCoordinator.Dispose();
        _registryAuthRequest.Dispose();
        _registryAvatarRequest.Dispose();
        PackageIconImageViewModelLoader.DisposeImage(RegistryAccountAvatarImage);
        RegistryAccountAvatarImage = null;
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
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        CancellationToken cancellationToken = default,
        Action? detachAuxiliaryPackageViews = null,
        Func<CancellationToken, Task>? refreshAuxiliaryPackageViewsAsync = null
    )
    {
        if (_disposed)
        {
            return;
        }

        SuspendPackageViewPreloading();
        try
        {
            await _packageViewPresentationCoordinator.ApplyPackageLifecycleChangesAsync(
                snapshot,
                packageSources,
                retryDisabledPackageIds,
                cancellationToken,
                detachAuxiliaryPackageViews,
                refreshAuxiliaryPackageViewsAsync
            );
        }
        finally
        {
            ResumePackageViewPreloading();
        }
    }

    internal Task ApplyPackageMetadataSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? refreshAuxiliaryPackageViewsAsync = null)
        => _packageViewPresentationCoordinator.ApplyPackageMetadataAsync(
            snapshot.ActivePackages,
            cancellationToken,
            refreshAuxiliaryPackageViewsAsync);

    internal void ConfigurePackageViewStagingSurface(
        Action<Control> stageCandidateView,
        Action detachStagedCandidateViews,
        Func<IDisposable?>? acquirePackageViewTransitionSnapshot = null
    )
    {
        _packageViewPresentationCoordinator.ConfigureStagingSurface(
            stageCandidateView,
            detachStagedCandidateViews,
            acquirePackageViewTransitionSnapshot);
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
        _packageViewPresentationCoordinator.ConfigureWorkScheduler(runPostPresentationWorkAsync);
        RestartPackageViewPreloading();
    }

    internal void ConfigurePackageViewWorkScheduler(
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runWorkAsync)
    {
        _packageViewPresentationCoordinator.ConfigureWorkScheduler(runWorkAsync);
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
        RequestPreloadCancellation(previousCancellation, "replacing package view preloading");
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
        RequestPreloadCancellation(cancellation, "cancelling package view preloading");
    }

    private void SuspendPackageViewPreloading()
    {
        CancellationTokenSource? cancellation;
        lock (_viewPreloadSyncRoot)
        {
            _viewPreloadSuspensionCount++;
            cancellation = _viewPreloadCancellation;
        }
        RequestPreloadCancellation(cancellation, "suspending package view preloading");
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
        try
        {
            return await _packageViewPresentationCoordinator.ReloadPackageViewAsync(
                _packagePanelCoordinator,
                viewId);
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
        if (!_packageViewPresentationCoordinator.CanStartPackageViewInteraction)
        {
            return false;
        }

        return await _packagePanelCoordinator.OpenPackageViewPanelAsync(viewId, parameters);
    }

    public bool ClosePackageViewPanel(string viewId)
    {
        if (!_packageViewPresentationCoordinator.CanStartPackageViewInteraction)
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

    private void RefreshPackageMetadata(IReadOnlySet<string> impactedPackageIds)
        => _railCollectionPresenter.RefreshPackageMetadata(
            _shellLayout.GetSlots(),
            impactedPackageIds);

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
        if (_packageViewPresentationCoordinator.CanStartPackageViewInteraction)
        {
            _packagePanelCoordinator.SelectItem(item, allowToggle);
        }
    }

    private void ObserveViewNavigation(string viewId)
        => _packageViewPresentationCoordinator.ObserveViewNavigation(viewId);

    private async ValueTask<bool> PresentPackageViewAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters)
        => await _packageViewPresentationCoordinator.PresentPackageViewAsync(
            viewId,
            parameters);

    private void ObservePanelClose(RailPlacement placement, string viewId)
        => _packageViewPresentationCoordinator.ObservePanelClose(placement, viewId);

    private void QueueViewNavigationCancellation(string viewId)
        => _packageViewPresentationCoordinator.QueueViewNavigationCancellation(viewId);

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

    private void RequestPreloadCancellation(
        CancellationTokenSource? cancellation,
        string operation)
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
            return;
        }

        _tasks.Observe(
            ObservePreloadCancellationAsync(cancellationTask, operation),
            operation);
    }

    private static async Task ObservePreloadCancellationAsync(
        Task cancellationTask,
        string operation)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Package callback failed while {operation}.", ex);
        }
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

        // Quarantine is temporary App-host state. Boundaries already show a host fallback;
        // retaining the projection avoids persisting the fault as an uninstall/layout change.
    }

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
