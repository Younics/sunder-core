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
    private Action<Control>? _stageCandidateView;
    private Action? _detachStagedCandidateViews;
    private bool _shellRevealed;
    private bool _shellPersistencePending;
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
            packageViewHostService.CancelViewNavigation
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
            PersistShellState
        );
        _deferredHostedViewActivator = new ShellDeferredHostedViewActivator(
            _shellState,
            () => _disposed,
            GetPanel,
            ApplyPanelContent,
            NotifyViewNavigatedIgnoringCancellationAsync,
            NotifyLayoutStateChanged,
            _uiDispatcher
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
            _uiDispatcher
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
        await _packageLifecycleRefreshCoordinator.ApplyPackageLifecycleChangesAsync(
            snapshot,
            retryDisabledPackageIds,
            lifetimeCancellation.Token,
            detachAuxiliaryPackageViews
        );
    }

    internal void ConfigurePackageViewStagingSurface(
        Action<Control> stageCandidateView,
        Action detachStagedCandidateViews
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
    ) => await _hotbarCoordinator.AddViewToDefaultHotbarAsync(viewId, openPanel, parameters);

    public async ValueTask<bool> AddPackageViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null
    ) =>
        await _hotbarCoordinator.AddViewToHotbarAsync(
            viewId,
            placement,
            index,
            openPanel,
            parameters
        );

    public bool RemovePackageViewFromHotbar(string viewId)
    {
        var removed = _hotbarCoordinator.RemoveViewFromHotbar(viewId);
        if (removed)
        {
            _packageViewHostService.CancelViewNavigation(viewId);
        }
        return removed;
    }

    public async ValueTask<bool> ReloadPackageViewAsync(string viewId) =>
        await _packagePanelCoordinator.ReloadPackageViewAsync(viewId);

    public async ValueTask<bool> OpenPackageViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null
    ) => await _packagePanelCoordinator.OpenPackageViewPanelAsync(viewId, parameters);

    public bool ClosePackageViewPanel(string viewId) =>
        _packagePanelCoordinator.ClosePackageViewPanel(viewId);

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

    private void SelectItem(ShellItemViewModel item, bool allowToggle) =>
        _packagePanelCoordinator.SelectItem(item, allowToggle);

    private void ObserveViewNavigation(string viewId) =>
        _tasks.Observe(
            NotifyViewNavigatedIgnoringCancellationAsync(viewId, _tasks.Token),
            $"navigating package view '{viewId}'"
        );

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
