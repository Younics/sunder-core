using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Protocol;
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
    private readonly ShellPackageLifecyclePresenter _packageLifecyclePresenter;
    private readonly ShellHotbarCoordinator _hotbarCoordinator;
    private readonly ShellLayoutPresenter _shellLayout;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly NotificationTrayViewModel _notificationTray;
    private readonly AppUpdatePromptViewModel _appUpdatePrompt;
    private readonly MainWindowSubscriptionScope _subscriptionScope;
    private readonly ShellSelectionPresenter _selectionPresenter = new();
    private readonly ShellItemViewModelFactory _shellItemFactory;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, ShellPackageView> _viewsById;
    private readonly IReadOnlyList<string> _startupWarnings;
    private readonly IReadOnlyList<string> _startupErrors;
    private bool _disposed;

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
        IShellCompositionService? shellCompositionService = null)
    {
        _windowLauncher = windowLauncher;
        var effectivePackageLifecycleCoordinator = packageLifecycleCoordinator ?? new AppPackageLifecycleCoordinator(packageViewHostService, runtimeApiClientFactory);
        var effectiveShellCompositionService = shellCompositionService ?? new ShellCompositionService();
        _appUpdatePrompt = new AppUpdatePromptViewModel(new AppUpdatePromptCoordinator(updateService ?? new SunderUpdateService()));
        _shellItemFactory = new ShellItemViewModelFactory(runtimeApiClientFactory);
        _shellState = shellSnapshot.State;
        _layoutStateCoordinator = new ShellLayoutStateCoordinator(
            shellStateService,
            _shellState,
            TimeSpan.FromMilliseconds(ShellStateSaveDelayMilliseconds));
        _viewsById = shellSnapshot.PackageViews.ToDictionary(x => x.ViewId, StringComparer.OrdinalIgnoreCase);
        _startupWarnings = shellSnapshot.StartupWarnings;
        _startupErrors = shellSnapshot.StartupErrors;
        _shellPanelContentPresenter = new ShellPanelContentPresenter(packageViewHostService, _startupWarnings, _startupErrors);
        _railCollectionPresenter = new ShellRailCollectionPresenter(
            _viewsById,
            _shellState,
            _selectionPresenter,
            _shellPanelContentPresenter,
            _shellItemFactory.Create);
        _runtimeStatus = new RuntimeStatusViewModel(
            runtimeConnectionState,
            runtimeApiClientFactory,
            runtimeHostProcessManager,
            shellSnapshot.SystemStatusText,
            initialSystemStatus,
            _startupErrors,
            _layoutStateCoordinator.PersistPreferredRuntimeUrl);
        _notificationTray = new NotificationTrayViewModel(notificationCenter);
        BackgroundProcesses = backgroundProcessQueue is null
            ? BackgroundProcessMonitorViewModel.Empty
            : new BackgroundProcessMonitorViewModel(
                backgroundProcessQueue,
                BackgroundProcessIndicator.Main,
                "No visible processes.",
                _shellState.BackgroundProcessPopoverWidth,
                _shellState.BackgroundProcessPopoverHeight,
                _layoutStateCoordinator.PersistBackgroundProcessPopoverSize);

        _shellLayout = new ShellLayoutPresenter(
            MovePackageView,
            ReloadPackageViewAsync,
            RemovePackageViewFromHotbar,
            ToggleLeftTopView,
            ToggleMiddleView,
            ToggleRightTopView,
            ToggleLeftBottomView,
            ToggleRightBottomView);
        _hotbarCoordinator = new ShellHotbarCoordinator(
            _viewsById,
            _shellState,
            GetOrderedViewIds,
            OpenPackageViewPanelAsync,
            RebuildRailCollections,
            PersistShellState);
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
            RebuildRailCollections,
            NotifyLayoutStateChanged,
            PersistShellState);
        _deferredHostedViewActivator = new ShellDeferredHostedViewActivator(
            _shellState,
            () => _disposed,
            GetPanel,
            ApplyPanelContent,
            NotifyLayoutStateChanged);
        _packageLifecyclePresenter = new ShellPackageLifecyclePresenter(
            effectiveShellCompositionService,
            _viewsById,
            _shellState,
            _startupWarnings,
            _startupErrors,
            status => SyncStatusText = status,
            RebuildRailCollections,
            PersistShellState);
        _packageLifecycleRefreshCoordinator = new ShellPackageLifecycleRefreshCoordinator(
            effectivePackageLifecycleCoordinator,
            _packageLifecyclePresenter,
            _deferredHostedViewActivator,
            () => _disposed);
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
            OnToastQueued);

        LeftPanelWidth = _shellState.LeftPanelWidth;
        RightPanelWidth = _shellState.RightPanelWidth;
        TopRowHeightRatio = _shellState.TopRowHeightRatio;
        BottomSplitRatio = _shellState.BottomSplitRatio;
        SyncStatusText = shellSnapshot.SyncStatusText;
        _notificationTray.ReloadNotifications();

        RebuildRailCollections(createHostedViews: !deferInitialHostedViews);
        PersistShellState();
    }

    public PackageIconBarViewModel LeftTopBar => _shellLayout.LeftTopBar;

    public PackageIconBarViewModel MiddleBar => _shellLayout.MiddleBar;

    public PackageIconBarViewModel RightTopBar => _shellLayout.RightTopBar;

    public PackageIconBarViewModel LeftBottomBar => _shellLayout.LeftBottomBar;

    public PackageIconBarViewModel RightBottomBar => _shellLayout.RightBottomBar;

    public BackgroundProcessMonitorViewModel BackgroundProcesses { get; }

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

    public bool HasBottomSplitPanelContent => HasLeftBottomPanelContent && HasRightBottomPanelContent;

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
    private void OpenSettings() => _windowLauncher.ShowSettings();

    public void AdjustLiveLeftPanelWidth(double delta, double maximumWidth)
    {
        LeftPanelWidth = _layoutStateCoordinator.AdjustLeftPanelWidth(LeftPanelWidth, delta, maximumWidth);
    }

    public void AdjustLiveRightPanelWidth(double delta, double maximumWidth)
    {
        RightPanelWidth = _layoutStateCoordinator.AdjustRightPanelWidth(RightPanelWidth, delta, maximumWidth);
    }

    public void AdjustLiveTopRowHeightRatio(double deltaRatio)
    {
        TopRowHeightRatio = _layoutStateCoordinator.AdjustTopRowHeightRatio(TopRowHeightRatio, deltaRatio);
    }

    public void AdjustLiveBottomSplitRatio(double deltaRatio)
    {
        BottomSplitRatio = _layoutStateCoordinator.AdjustBottomSplitRatio(BottomSplitRatio, deltaRatio);
    }

    public void CommitLayoutState() => PersistShellState();

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscriptionScope.Dispose();
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<PackageViewMenuGroup> GetPackageViewGroups()
        => PackageViewMenuProjector.Project(_viewsById.Values, _shellItemFactory.CreatePackageIconUri, _hotbarCoordinator.IsViewInHotbar);

    public async Task ApplyPackageLifecycleChangesAsync(
        IReadOnlyCollection<string>? impactedPackageIds = null,
        CancellationToken cancellationToken = default,
        bool deferHostedViewCreation = false)
        => await _packageLifecycleRefreshCoordinator.ApplyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken, deferHostedViewCreation);

    public async Task ActivateDeferredInitialHostedViewsAsync(CancellationToken cancellationToken = default)
        => await _deferredHostedViewActivator.ActivateInitialHostedViewsAsync(cancellationToken);

    public void ActivatePackageView(string viewId)
        => _ = OpenPackageViewPanelAsync(viewId);

    public IReadOnlyList<PackageHotbarView> ListHotbarViews()
        => PackageHotbarProjector.Project(GetOrderedViewsForPlacement, placement => ShellSelectionState.GetSelectedViewId(_shellState, placement));

    public bool IsViewInHotbar(string viewId)
        => _hotbarCoordinator.IsViewInHotbar(viewId);

    public async ValueTask<bool> AddPackageViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
        => await _hotbarCoordinator.AddViewToDefaultHotbarAsync(viewId, openPanel, parameters);

    public async ValueTask<bool> AddPackageViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
        => await _hotbarCoordinator.AddViewToHotbarAsync(viewId, placement, index, openPanel, parameters);

    public bool RemovePackageViewFromHotbar(string viewId)
        => _hotbarCoordinator.RemoveViewFromHotbar(viewId);

    public async ValueTask<bool> ReloadPackageViewAsync(string viewId)
        => await _packagePanelCoordinator.ReloadPackageViewAsync(viewId);

    public async ValueTask<bool> OpenPackageViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null)
        => await _packagePanelCoordinator.OpenPackageViewPanelAsync(viewId, parameters);

    public bool ClosePackageViewPanel(string viewId)
        => _packagePanelCoordinator.ClosePackageViewPanel(viewId);

    public void MovePackageView(string viewId, RailPlacement placement, int? targetIndex)
        => _hotbarCoordinator.MoveView(viewId, placement, targetIndex);

    private void RebuildRailCollections(bool createHostedViews = true)
    {
        _railCollectionPresenter.Rebuild(_shellLayout.GetSlots(), createHostedViews);
        NotifyLayoutStateChanged();
    }

    private IEnumerable<ShellPackageView> GetOrderedViewsForPlacement(RailPlacement placement)
        => ShellViewOrdering.GetOrderedViewsForPlacement(_viewsById.Values, _shellState, placement);

    private List<string> GetOrderedViewIds(RailPlacement placement)
    {
        return GetOrderedViewsForPlacement(placement).Select(view => view.ViewId).ToList();
    }

    private void ToggleLeftTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleMiddleView(ShellItemViewModel item) => SelectItem(item, allowToggle: false);

    private void ToggleRightTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleLeftBottomView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleRightBottomView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void SelectItem(ShellItemViewModel item, bool allowToggle)
        => _packagePanelCoordinator.SelectItem(item, allowToggle);

    private void ApplyPanelContent(RailPlacement placement, string? viewId, bool createHostedView = true)
    {
        var panel = GetPanel(placement);
        _shellPanelContentPresenter.Apply(panel, placement, viewId, _viewsById, MiddleBar.Items.Count, createHostedView);
    }

    private void OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess() && Application.Current is not null)
        {
            Dispatcher.UIThread.Post(() => OnPackageFaulted(sender, e));
            return;
        }

        if (!_packageLifecyclePresenter.RemovePackageViewsFromShell(e.PackageId))
        {
            return;
        }

        RebuildRailCollections();
        PersistShellState();
    }

    private void PersistShellState()
        => _layoutStateCoordinator.PersistShellLayout(LeftPanelWidth, RightPanelWidth, TopRowHeightRatio, BottomSplitRatio);

    private PackageIconBarViewModel GetBar(RailPlacement placement)
        => _shellLayout.GetBar(placement);

    private ShellPanelViewModel GetPanel(RailPlacement placement)
        => _shellLayout.GetPanel(placement);

    private void NotifyLayoutStateChanged()
    {
        OnPropertyChanged(nameof(HasLeftTopPanelContent));
        OnPropertyChanged(nameof(HasMiddleSelection));
        OnPropertyChanged(nameof(HasRightTopPanelContent));
        OnPropertyChanged(nameof(HasLeftBottomPanelContent));
        OnPropertyChanged(nameof(HasRightBottomPanelContent));
        OnPropertyChanged(nameof(HasAnyBottomPanelContent));
        OnPropertyChanged(nameof(HasBottomSplitPanelContent));
        ShellViewStateChanged?.Invoke();
    }

}
