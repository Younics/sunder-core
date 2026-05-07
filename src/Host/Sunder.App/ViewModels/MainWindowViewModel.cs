using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int ShellStateSaveDelayMilliseconds = 250;
    private const double MinimumPanelWidth = 180;
    private const double MaximumPanelWidth = 1200;
    private const double MaximumTopRowRatio = 1.0;
    private static readonly IBrush RuntimeReadyBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush RuntimeWarningBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush RuntimeErrorBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush RuntimeUnavailableBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush RuntimeBusyBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

    private readonly IWindowLauncher _windowLauncher;
    private readonly ShellStateService _shellStateService;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly AppPackageShellViewService? _shellViewService;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly RuntimeHostProcessManager _runtimeHostProcessManager;
    private readonly NotificationCenterService _notificationCenter;
    private readonly SunderUpdateService _updateService;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, ShellPackageView> _viewsById;
    private readonly IReadOnlyList<string> _startupWarnings;
    private readonly IReadOnlyList<string> _startupErrors;
    private CancellationTokenSource? _pendingShellStateSaveCts;
    private bool _disposed;
    private ShellItemViewModel? _selectedLeftTopItem;
    private ShellItemViewModel? _selectedMiddleItem;
    private ShellItemViewModel? _selectedRightTopItem;
    private ShellItemViewModel? _selectedLeftBottomItem;
    private ShellItemViewModel? _selectedRightBottomItem;

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
        SunderUpdateService? updateService = null)
    {
        _windowLauncher = windowLauncher;
        _shellStateService = shellStateService;
        _packageViewHostService = packageViewHostService;
        _shellViewService = shellViewService;
        _runtimeConnectionState = runtimeConnectionState;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _runtimeHostProcessManager = runtimeHostProcessManager;
        _notificationCenter = notificationCenter;
        _updateService = updateService ?? new SunderUpdateService();
        _shellState = shellSnapshot.State;
        _viewsById = shellSnapshot.PackageViews.ToDictionary(x => x.ViewId, StringComparer.OrdinalIgnoreCase);
        _startupWarnings = shellSnapshot.StartupWarnings;
        _startupErrors = shellSnapshot.StartupErrors;
        _packageViewHostService.PackageFaulted += OnPackageFaulted;
        _notificationCenter.NotificationsChanged += OnNotificationsChanged;
        _notificationCenter.ToastQueued += OnToastQueued;
        if (_windowLauncher is WindowLauncher concreteWindowLauncher)
        {
            concreteWindowLauncher.AttachShell(this);
        }

        LeftTopBar = new PackageIconBarViewModel(RailPlacement.LeftTop, Orientation.Vertical, MovePackageView);
        MiddleBar = new PackageIconBarViewModel(RailPlacement.Middle, Orientation.Horizontal, MovePackageView);
        RightTopBar = new PackageIconBarViewModel(RailPlacement.RightTop, Orientation.Vertical, MovePackageView);
        LeftBottomBar = new PackageIconBarViewModel(RailPlacement.LeftBottom, Orientation.Vertical, MovePackageView);
        RightBottomBar = new PackageIconBarViewModel(RailPlacement.RightBottom, Orientation.Vertical, MovePackageView);

        LeftTopPanel = new ShellPanelViewModel();
        MiddlePanel = new ShellPanelViewModel();
        RightTopPanel = new ShellPanelViewModel();
        LeftBottomPanel = new ShellPanelViewModel();
        RightBottomPanel = new ShellPanelViewModel();

        LeftPanelWidth = _shellState.LeftPanelWidth;
        RightPanelWidth = _shellState.RightPanelWidth;
        TopRowHeightRatio = _shellState.TopRowHeightRatio;
        BottomSplitRatio = _shellState.BottomSplitRatio;
        SystemStatusText = shellSnapshot.SystemStatusText;
        SyncStatusText = shellSnapshot.SyncStatusText;
        RuntimeAddressText = _runtimeConnectionState.RuntimeUrl.AbsoluteUri;
        ApplyInitialRuntimeState(initialSystemStatus);
        ReloadNotifications();
        _shellViewService?.Attach(this);

        RebuildRailCollections();
        PersistShellState();
    }

    public PackageIconBarViewModel LeftTopBar { get; }

    public PackageIconBarViewModel MiddleBar { get; }

    public PackageIconBarViewModel RightTopBar { get; }

    public PackageIconBarViewModel LeftBottomBar { get; }

    public PackageIconBarViewModel RightBottomBar { get; }

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    public ObservableCollection<ToastNotificationViewModel> Toasts { get; } = [];

    public bool CanInstallAppUpdate => ShowUpdatePrompt && !IsUpdateActionBusy;

    [ObservableProperty]
    private bool _showUpdatePrompt;

    [ObservableProperty]
    private bool _isUpdateActionBusy;

    [ObservableProperty]
    private string _updatePromptMessage = string.Empty;

    [ObservableProperty]
    private string _updatePromptStatus = string.Empty;

    private SunderUpdateInfo? _availableAppUpdate;

    public ShellPanelViewModel LeftTopPanel { get; }

    public ShellPanelViewModel MiddlePanel { get; }

    public ShellPanelViewModel RightTopPanel { get; }

    public ShellPanelViewModel LeftBottomPanel { get; }

    public ShellPanelViewModel RightBottomPanel { get; }

    public bool HasLeftTopPanelContent => _selectedLeftTopItem is not null;

    public bool HasMiddleSelection => _selectedMiddleItem is not null;

    public bool HasRightTopPanelContent => _selectedRightTopItem is not null;

    public bool HasLeftBottomPanelContent => _selectedLeftBottomItem is not null;

    public bool HasRightBottomPanelContent => _selectedRightBottomItem is not null;

    public bool HasAnyBottomPanelContent => HasLeftBottomPanelContent || HasRightBottomPanelContent;

    [ObservableProperty]
    private double _leftPanelWidth = ShellState.DefaultLeftPanelWidth;

    [ObservableProperty]
    private double _rightPanelWidth = ShellState.DefaultRightPanelWidth;

    [ObservableProperty]
    private double _topRowHeightRatio = ShellState.DefaultTopRowHeightRatio;

    [ObservableProperty]
    private double _bottomSplitRatio = ShellState.DefaultBottomSplitRatio;

    [ObservableProperty]
    private string _systemStatusText = "System Ready";

    [ObservableProperty]
    private string _syncStatusText = "Synced";

    [ObservableProperty]
    private string _runtimeAddressText = string.Empty;

    [ObservableProperty]
    private string _runtimeName = "Sunder Server";

    [ObservableProperty]
    private string _runtimeVersion = "Unknown";

    [ObservableProperty]
    private string _runtimeStatusText = "Runtime unavailable";

    [ObservableProperty]
    private string _runtimeLastError = string.Empty;

    [ObservableProperty]
    private bool _isRuntimeRunning;

    [ObservableProperty]
    private bool _isRuntimeReady;

    [ObservableProperty]
    private bool _isRuntimeBusy;

    [ObservableProperty]
    private IBrush _runtimeStatusBrush = RuntimeUnavailableBrush;

    public bool CanManageRuntime => !IsRuntimeBusy;

    public bool ShowRuntimeAddressEditor => !IsRuntimeRunning;

    public bool ShowApplyRuntimeButton => !IsRuntimeRunning;

    public bool ShowStartRuntimeButton => !IsRuntimeRunning;

    public bool ShowStopRuntimeButton => IsRuntimeRunning;

    public bool ShowRuntimeError => !string.IsNullOrWhiteSpace(RuntimeLastError);

    public bool HasNotifications => Notifications.Count > 0;

    public bool HasNoNotifications => !HasNotifications;

    [ObservableProperty]
    private bool _hasUnreadNotifications;

    partial void OnIsRuntimeBusyChanged(bool value) => OnPropertyChanged(nameof(CanManageRuntime));

    partial void OnIsRuntimeRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRuntimeAddressEditor));
        OnPropertyChanged(nameof(ShowApplyRuntimeButton));
        OnPropertyChanged(nameof(ShowStartRuntimeButton));
        OnPropertyChanged(nameof(ShowStopRuntimeButton));
    }

    partial void OnRuntimeLastErrorChanged(string value) => OnPropertyChanged(nameof(ShowRuntimeError));

    partial void OnShowUpdatePromptChanged(bool value) => OnPropertyChanged(nameof(CanInstallAppUpdate));

    partial void OnIsUpdateActionBusyChanged(bool value) => OnPropertyChanged(nameof(CanInstallAppUpdate));

    public void MarkNotificationsRead()
        => _notificationCenter.MarkAllRead();

    public async Task CheckForAppUpdatesOnStartupAsync()
    {
        try
        {
            var updateSettings = _updateService.LoadSettings();
            var checkResult = await _updateService.CheckForUpdatesAsync();
            if (checkResult.Update is null)
            {
                return;
            }

            if (updateSettings.DownloadUpdatesAutomatically)
            {
                await _updateService.DownloadUpdateAsync(checkResult.Update);
                return;
            }

            RunOnUiThread(() => ShowAppUpdatePrompt(checkResult.Update));
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Sunder app startup update check failed.", ex);
        }
    }

    [RelayCommand]
    private void OpenPackages() => _windowLauncher.ShowPackages();

    [RelayCommand]
    private void OpenSettings() => _windowLauncher.ShowSettings();

    [RelayCommand]
    private async Task InstallAvailableAppUpdateAsync()
    {
        if (_availableAppUpdate is null || IsUpdateActionBusy)
        {
            return;
        }

        IsUpdateActionBusy = true;
        UpdatePromptStatus = "Downloading update...";
        try
        {
            await _updateService.DownloadUpdateAndRestartAsync(
                _availableAppUpdate,
                progress => RunOnUiThread(() => UpdatePromptStatus = $"Downloading update... {progress}%"));
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to install Sunder app update.", ex);
            UpdatePromptStatus = $"Update failed: {ex.Message}";
            IsUpdateActionBusy = false;
        }
    }

    [RelayCommand]
    private void DismissAppUpdatePrompt()
    {
        if (IsUpdateActionBusy)
        {
            return;
        }

        _availableAppUpdate = null;
        ShowUpdatePrompt = false;
        UpdatePromptMessage = string.Empty;
        UpdatePromptStatus = string.Empty;
    }

    [RelayCommand]
    private async Task RefreshRuntimeAsync()
    {
        if (!TrySyncRuntimeAddressFromText(persistPreference: false, out _))
        {
            return;
        }

        await RefreshRuntimeStateAsync();
    }

    [RelayCommand]
    private async Task ApplyRuntimeAddressAsync()
    {
        if (!TrySyncRuntimeAddressFromText(persistPreference: true, out _))
        {
            return;
        }

        await RefreshRuntimeStateAsync();
    }

    [RelayCommand]
    private async Task StartRuntimeAsync()
    {
        if (!TrySyncRuntimeAddressFromText(persistPreference: true, out var runtimeUrl) || runtimeUrl is null)
        {
            return;
        }

        IsRuntimeBusy = true;
        RuntimeLastError = string.Empty;
        RuntimeStatusText = "Starting runtime...";
        SystemStatusText = RuntimeStatusText;
        RuntimeStatusBrush = RuntimeBusyBrush;

        try
        {
            await _runtimeHostProcessManager.EnsureStartedAsync(runtimeUrl);
        }
        catch (Exception ex)
        {
            SetRuntimeState(
                isRunning: false,
                isReady: false,
                name: "Sunder Server",
                version: "Unknown",
                statusText: "Runtime unavailable",
                lastError: ex.Message);
            return;
        }
        finally
        {
            IsRuntimeBusy = false;
        }

        await RefreshRuntimeStateAsync();
    }

    [RelayCommand]
    private async Task StopRuntimeAsync()
    {
        if (!TrySyncRuntimeAddressFromText(persistPreference: true, out _))
        {
            return;
        }

        IsRuntimeBusy = true;
        RuntimeLastError = string.Empty;
        RuntimeStatusText = "Stopping runtime...";
        SystemStatusText = RuntimeStatusText;
        RuntimeStatusBrush = RuntimeBusyBrush;

        try
        {
            using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
            await runtimeApiClient.ShutdownAsync();
            await Task.Delay(250);
        }
        catch
        {
            await Task.Delay(250);
        }
        finally
        {
            IsRuntimeBusy = false;
        }

        await RefreshRuntimeStateAsync();
    }

    private void ApplyInitialRuntimeState(SystemStatusResponse? initialSystemStatus)
    {
        if (initialSystemStatus is not null)
        {
            SetRuntimeState(
                isRunning: true,
                isReady: initialSystemStatus.IsReady,
                name: initialSystemStatus.Name,
                version: initialSystemStatus.Version,
                statusText: initialSystemStatus.IsReady ? "Runtime ready" : "Runtime running",
                lastError: string.Empty);
            return;
        }

        SetRuntimeState(
            isRunning: false,
            isReady: false,
            name: "Sunder Server",
            version: "Unknown",
            statusText: "Runtime unavailable",
            lastError: _startupErrors.FirstOrDefault() ?? string.Empty);
    }

    private async Task RefreshRuntimeStateAsync()
    {
        IsRuntimeBusy = true;
        RuntimeLastError = string.Empty;
        RuntimeStatusText = "Checking runtime...";
        SystemStatusText = RuntimeStatusText;
        RuntimeStatusBrush = RuntimeBusyBrush;

        using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
        try
        {
            var systemStatus = await runtimeApiClient.GetSystemStatusAsync();
            if (systemStatus is not null)
            {
                SetRuntimeState(
                    isRunning: true,
                    isReady: systemStatus.IsReady,
                    name: systemStatus.Name,
                    version: systemStatus.Version,
                    statusText: systemStatus.IsReady ? "Runtime ready" : "Runtime running",
                    lastError: string.Empty);
                return;
            }
        }
        catch (Exception ex)
        {
            var isHealthy = await runtimeApiClient.IsRuntimeHealthyAsync();
            SetRuntimeState(
                isRunning: isHealthy,
                isReady: false,
                name: "Sunder Server",
                version: "Unknown",
                statusText: isHealthy ? "Runtime API error" : "Runtime unavailable",
                lastError: ex.Message);
            IsRuntimeBusy = false;
            return;
        }

        var isRuntimeHealthy = await runtimeApiClient.IsRuntimeHealthyAsync();
        SetRuntimeState(
            isRunning: isRuntimeHealthy,
            isReady: false,
            name: "Sunder Server",
            version: "Unknown",
            statusText: isRuntimeHealthy ? "Runtime running" : "Runtime unavailable",
            lastError: string.Empty);
        IsRuntimeBusy = false;
    }

    private bool TrySyncRuntimeAddressFromText(bool persistPreference, out Uri? runtimeUrl)
    {
        if (!RuntimeUrlHelper.TryParse(RuntimeAddressText, out runtimeUrl) || runtimeUrl is null)
        {
            RuntimeLastError = $"'{RuntimeAddressText}' is not a valid HTTP runtime URL.";
            SystemStatusText = "Runtime address invalid";
            RuntimeStatusBrush = RuntimeErrorBrush;
            return false;
        }

        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        RuntimeAddressText = runtimeUrl.AbsoluteUri;

        if (persistPreference)
        {
            _shellState.PreferredRuntimeUrl = runtimeUrl.AbsoluteUri;
            PersistShellState();
        }

        return true;
    }

    private void SetRuntimeState(bool isRunning, bool isReady, string name, string version, string statusText, string lastError)
    {
        IsRuntimeRunning = isRunning;
        IsRuntimeReady = isReady;
        RuntimeName = name;
        RuntimeVersion = version;
        RuntimeStatusText = statusText;
        RuntimeLastError = lastError;
        SystemStatusText = statusText;
        RuntimeStatusBrush = ResolveRuntimeStatusBrush(isRunning, isReady, lastError);
        IsRuntimeBusy = false;
    }

    private static IBrush ResolveRuntimeStatusBrush(bool isRunning, bool isReady, string lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return isRunning ? RuntimeWarningBrush : RuntimeErrorBrush;
        }

        if (isReady)
        {
            return RuntimeReadyBrush;
        }

        return isRunning ? RuntimeWarningBrush : RuntimeUnavailableBrush;
    }

    public void AdjustLiveLeftPanelWidth(double delta, double maximumWidth)
    {
        LeftPanelWidth = ClampPanelWidth(LeftPanelWidth + delta, maximumWidth);
    }

    public void AdjustLiveRightPanelWidth(double delta, double maximumWidth)
    {
        RightPanelWidth = ClampPanelWidth(RightPanelWidth + delta, maximumWidth);
    }

    public void AdjustLiveTopRowHeightRatio(double deltaRatio)
    {
        TopRowHeightRatio = ClampTopRowRatio(TopRowHeightRatio + deltaRatio);
    }

    public void AdjustLiveBottomSplitRatio(double deltaRatio)
    {
        BottomSplitRatio = Math.Clamp(BottomSplitRatio + deltaRatio, 0.01, 0.99);
    }

    public void CommitLayoutState() => PersistShellState();

    private void CollapsePlacement(RailPlacement placement)
    {
        ref var selectedItem = ref GetSelectedItemReference(placement);
        ClearSelection(GetBar(placement).Items, ref selectedItem);
        SetSelectedViewId(placement, null);
        ApplyPanelContent(placement, null);
    }

    private static double ClampPanelWidth(double value, double maximumWidth)
    {
        var upperBound = double.IsNaN(maximumWidth) || double.IsInfinity(maximumWidth) || maximumWidth <= 0
            ? MaximumPanelWidth
            : Math.Min(maximumWidth, MaximumPanelWidth);
        upperBound = Math.Max(MinimumPanelWidth, upperBound);
        return Math.Clamp(value, MinimumPanelWidth, upperBound);
    }

    private static double ClampTopRowRatio(double value) => Math.Clamp(value, 0, MaximumTopRowRatio);

    private void OnNotificationsChanged()
        => RunOnUiThread(ReloadNotifications);

    private void OnToastQueued(AppToastNotification notification)
        => RunOnUiThread(() => AddToast(notification));

    private void ReloadNotifications()
    {
        var lastReadAtUtc = _notificationCenter.LastReadAtUtc;
        Notifications.Clear();
        foreach (var notification in _notificationCenter.ListNotifications())
        {
            Notifications.Add(new NotificationItemViewModel(notification, lastReadAtUtc));
        }

        HasUnreadNotifications = _notificationCenter.HasUnreadTrayNotifications();
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasNoNotifications));
    }

    private void AddToast(AppToastNotification notification)
    {
        if (_disposed)
        {
            return;
        }

        while (Toasts.Count >= 3)
        {
            Toasts.RemoveAt(0);
        }

        var toast = new ToastNotificationViewModel(notification);
        Toasts.Add(toast);
        _ = DismissToastAsync(toast);
    }

    private void ShowAppUpdatePrompt(SunderUpdateInfo update)
    {
        _availableAppUpdate = update;
        UpdatePromptMessage = $"A new version of Sunder ({update.Version}) is now available to install.";
        UpdatePromptStatus = "Install now or skip until the next app start.";
        ShowUpdatePrompt = true;
        IsUpdateActionBusy = false;
    }

    [RelayCommand]
    private void DismissToastNotification(ToastNotificationViewModel? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    private async Task DismissToastAsync(ToastNotificationViewModel toast)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5));
        }
        catch
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_disposed)
            {
                Toasts.Remove(toast);
            }
        });
    }

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
        _packageViewHostService.PackageFaulted -= OnPackageFaulted;
        _notificationCenter.NotificationsChanged -= OnNotificationsChanged;
        _notificationCenter.ToastQueued -= OnToastQueued;
        if (_windowLauncher is WindowLauncher concreteWindowLauncher)
        {
            concreteWindowLauncher.DetachShell(this);
        }
        _shellViewService?.Detach(this);
        _windowLauncher.CloseForShutdown();

        _pendingShellStateSaveCts?.Cancel();
        _pendingShellStateSaveCts?.Dispose();
        _pendingShellStateSaveCts = null;

        SaveShellStateImmediately();
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<PackageViewMenuGroup> GetPackageViewGroups()
    {
        return _viewsById.Values
            .OrderBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase)
            .GroupBy(view => view.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new PackageViewMenuGroup(
                    first.PackageId,
                    first.PackageDisplayName,
                    group.Select(view => new PackageViewMenuItem(view.ViewId, view.Title, view.Placement)).ToArray());
            })
            .ToArray();
    }

    public async Task ApplyPackageLifecycleChangesAsync(
        IReadOnlyCollection<string>? impactedPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
        var activePackagesTask = runtimeApiClient.GetActivePackagesAsync(cancellationToken);
        var packageSourcesTask = runtimeApiClient.GetActivePackageSourcesAsync(cancellationToken);
        await Task.WhenAll(activePackagesTask, packageSourcesTask);

        var activePackages = await activePackagesTask;
        var packageSources = await packageSourcesTask;
        var activePackageIds = activePackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPackageIds = _viewsById.Values
            .Select(view => view.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(packageId => !activePackageIds.Contains(packageId))
            .ToArray();
        var impactedPackages = impactedPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(impactedPackageIds, StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in removedPackageIds)
        {
            RemovePackageViewsFromShell(packageId);
        }

        if (removedPackageIds.Length > 0)
        {
            RebuildRailCollections();
        }

        await Task.Run(
            async () => await _packageViewHostService.ApplyPackageDeltaAsync(activePackages, packageSources, impactedPackages, cancellationToken),
            cancellationToken);

        var enabledPackages = _packageViewHostService.FilterEnabledPackages(activePackages);
        ApplyActivePackagesToShell(enabledPackages);
    }

    public void ActivatePackageView(string viewId)
        => _ = OpenPackageViewPanelAsync(viewId);

    public IReadOnlyList<PackageHotbarView> ListHotbarViews()
    {
        var views = new List<PackageHotbarView>();
        foreach (var placement in Enum.GetValues<RailPlacement>())
        {
            var order = 0;
            foreach (var view in GetOrderedViewsForPlacement(placement))
            {
                views.Add(new PackageHotbarView(
                    view.ViewId,
                    view.PackageId,
                    view.PackageDisplayName,
                    view.Title,
                    view.Glyph,
                    ToPackageHotbarPlacement(view.Placement),
                    order++,
                    string.Equals(GetSelectedViewId(placement), view.ViewId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return views;
    }

    public bool IsViewInHotbar(string viewId)
        => _viewsById.ContainsKey(viewId) && !_shellState.HiddenHotbarViewIds.Contains(viewId);

    public async ValueTask<bool> AddPackageViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        return await AddPackageViewToHotbarAsync(viewId, ToPackageHotbarPlacement(packageView.Placement), null, openPanel, parameters);
    }

    public async ValueTask<bool> AddPackageViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var targetPlacement = ToRailPlacement(placement);
        var sourcePlacement = packageView.Placement;
        var sourceOrder = GetOrderedViewIds(sourcePlacement);
        sourceOrder.RemoveAll(id => string.Equals(id, viewId, StringComparison.OrdinalIgnoreCase));

        var targetOrder = sourcePlacement == targetPlacement
            ? sourceOrder
            : GetOrderedViewIds(targetPlacement);
        targetOrder.RemoveAll(id => string.Equals(id, viewId, StringComparison.OrdinalIgnoreCase));
        InsertAt(targetOrder, viewId, index);

        _shellState.HiddenHotbarViewIds.Remove(viewId);
        _shellState.ViewPlacements[viewId] = targetPlacement;
        _viewsById[viewId] = packageView with { Placement = targetPlacement };

        SetOrderForPlacement(sourcePlacement, sourceOrder);
        if (sourcePlacement != targetPlacement)
        {
            SetOrderForPlacement(targetPlacement, targetOrder);
            ClearSelectionForPlacement(sourcePlacement, viewId);
        }
        else
        {
            SetOrderForPlacement(targetPlacement, targetOrder);
        }

        if (openPanel)
        {
            RebuildRailCollections();
            return await OpenPackageViewPanelAsync(viewId, parameters);
        }

        RebuildRailCollections();
        PersistShellState();
        return true;
    }

    public bool RemovePackageViewFromHotbar(string viewId)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        _shellState.HiddenHotbarViewIds.Add(viewId);
        ClearSelectionForPlacement(packageView.Placement, viewId);
        RebuildRailCollections();
        PersistShellState();
        return true;
    }

    public async ValueTask<bool> OpenPackageViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        if (!IsViewInHotbar(viewId)
            && !await AddPackageViewToDefaultHotbarAsync(viewId, openPanel: false, parameters: parameters))
        {
            return false;
        }

        packageView = _viewsById[viewId];
        var item = GetBar(packageView.Placement).Items.FirstOrDefault(x => x.Id == viewId);
        if (item is null)
        {
            RebuildRailCollections();
            item = GetBar(packageView.Placement).Items.FirstOrDefault(x => x.Id == viewId);
        }

        if (item is null)
        {
            return false;
        }

        if (!string.Equals(GetSelectedViewId(packageView.Placement), viewId, StringComparison.OrdinalIgnoreCase))
        {
            SelectItem(item, allowToggle: false);
        }

        await _packageViewHostService.NotifyViewNavigatedAsync(viewId, parameters);
        return true;
    }

    public bool ClosePackageViewPanel(string viewId)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        if (!string.Equals(GetSelectedViewId(packageView.Placement), viewId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var items = GetBar(packageView.Placement).Items;
        ref var selectedItem = ref GetSelectedItemReference(packageView.Placement);
        ClearSelection(items, ref selectedItem);
        SetSelectedViewId(packageView.Placement, null);
        ApplyPanelContent(packageView.Placement, null);
        NotifyLayoutStateChanged();
        PersistShellState();
        return true;
    }

    public void MovePackageView(string viewId, RailPlacement placement, int? targetIndex)
    {
        if (!_viewsById.TryGetValue(viewId, out var packageView))
        {
            return;
        }

        var sourcePlacement = packageView.Placement;
        if (!IsViewInHotbar(viewId))
        {
            _ = AddPackageViewToHotbarAsync(viewId, ToPackageHotbarPlacement(placement), targetIndex, openPanel: true);
            return;
        }

        var sourceOrder = GetOrderedViewIds(sourcePlacement);
        var originalIndex = sourceOrder.IndexOf(viewId);
        if (originalIndex < 0)
        {
            return;
        }

        sourceOrder.RemoveAt(originalIndex);

        if (sourcePlacement == placement)
        {
            var normalizedIndex = targetIndex.HasValue
                ? Math.Clamp(targetIndex.Value, 0, sourceOrder.Count)
                : sourceOrder.Count;

            if (normalizedIndex == originalIndex)
            {
                return;
            }

            InsertAt(sourceOrder, viewId, normalizedIndex);
            SetOrderForPlacement(placement, sourceOrder);
            RebuildRailCollections();
            PersistShellState();
            return;
        }

        var targetOrder = GetOrderedViewIds(placement);
        InsertAt(targetOrder, viewId, targetIndex);

        _shellState.ViewPlacements[viewId] = placement;
        _viewsById[viewId] = packageView with { Placement = placement };

        ClearSelectionForPlacement(sourcePlacement, viewId);
        SetSelectedViewId(placement, viewId);
        SetOrderForPlacement(sourcePlacement, sourceOrder);
        SetOrderForPlacement(placement, targetOrder);

        RebuildRailCollections();
        PersistShellState();
    }

    private void RebuildRailCollections()
    {
        _selectedLeftTopItem = null;
        _selectedMiddleItem = null;
        _selectedRightTopItem = null;
        _selectedLeftBottomItem = null;
        _selectedRightBottomItem = null;

        LeftTopPanel.HostedView = null;
        MiddlePanel.HostedView = null;
        RightTopPanel.HostedView = null;
        LeftBottomPanel.HostedView = null;
        RightBottomPanel.HostedView = null;

        LeftTopBar.SetItems(CreateItemsForPlacement(RailPlacement.LeftTop, ToggleLeftTopView));
        MiddleBar.SetItems(CreateItemsForPlacement(RailPlacement.Middle, ToggleMiddleView));
        RightTopBar.SetItems(CreateItemsForPlacement(RailPlacement.RightTop, ToggleRightTopView));
        LeftBottomBar.SetItems(CreateItemsForPlacement(RailPlacement.LeftBottom, ToggleLeftBottomView));
        RightBottomBar.SetItems(CreateItemsForPlacement(RailPlacement.RightBottom, ToggleRightBottomView));

        SetOrderForPlacement(RailPlacement.LeftTop, LeftTopBar.Items.Select(item => item.Id).ToArray());
        SetOrderForPlacement(RailPlacement.Middle, MiddleBar.Items.Select(item => item.Id).ToArray());
        SetOrderForPlacement(RailPlacement.RightTop, RightTopBar.Items.Select(item => item.Id).ToArray());
        SetOrderForPlacement(RailPlacement.LeftBottom, LeftBottomBar.Items.Select(item => item.Id).ToArray());
        SetOrderForPlacement(RailPlacement.RightBottom, RightBottomBar.Items.Select(item => item.Id).ToArray());

        RestoreSelection(RailPlacement.LeftTop, _shellState.SelectedLeftTopViewId, ref _selectedLeftTopItem);
        RestoreSelection(RailPlacement.Middle, _shellState.SelectedMiddleViewId, ref _selectedMiddleItem);
        RestoreSelection(RailPlacement.RightTop, _shellState.SelectedRightTopViewId, ref _selectedRightTopItem);
        RestoreSelection(RailPlacement.LeftBottom, _shellState.SelectedLeftBottomViewId, ref _selectedLeftBottomItem);
        RestoreSelection(RailPlacement.RightBottom, _shellState.SelectedRightBottomViewId, ref _selectedRightBottomItem);

        ApplyPanelContent(RailPlacement.LeftTop, _shellState.SelectedLeftTopViewId);
        ApplyPanelContent(RailPlacement.Middle, _shellState.SelectedMiddleViewId);
        ApplyPanelContent(RailPlacement.RightTop, _shellState.SelectedRightTopViewId);
        ApplyPanelContent(RailPlacement.LeftBottom, _shellState.SelectedLeftBottomViewId);
        ApplyPanelContent(RailPlacement.RightBottom, _shellState.SelectedRightBottomViewId);

        NotifyLayoutStateChanged();
    }

    private IReadOnlyList<ShellItemViewModel> CreateItemsForPlacement(RailPlacement placement, Action<ShellItemViewModel> onSelect)
    {
        return GetOrderedViewsForPlacement(placement)
            .Select(packageView => CreateShellItem(packageView, onSelect))
            .ToArray();
    }

    private IEnumerable<ShellPackageView> GetOrderedViewsForPlacement(RailPlacement placement)
    {
        return _viewsById.Values
            .Where(view => view.Placement == placement)
            .Where(view => !_shellState.HiddenHotbarViewIds.Contains(view.ViewId))
            .OrderBy(view => _shellState.ViewOrder.TryGetValue(view.ViewId, out var order) ? 0 : 1)
            .ThenBy(view => _shellState.ViewOrder.TryGetValue(view.ViewId, out var order) ? order : int.MaxValue)
            .ThenBy(view => view.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Title, StringComparer.OrdinalIgnoreCase);
    }

    private List<string> GetOrderedViewIds(RailPlacement placement)
    {
        return GetOrderedViewsForPlacement(placement).Select(view => view.ViewId).ToList();
    }

    private void RestoreSelection(RailPlacement placement, string? selectedId, ref ShellItemViewModel? selectedItem)
    {
        var items = GetBar(placement).Items;
        var selected = string.IsNullOrWhiteSpace(selectedId)
            ? null
            : items.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected is null && placement == RailPlacement.Middle && items.Count > 0 && !_shellState.HasInitializedLayout)
        {
            selected = items[0];
            SetSelectedViewId(placement, selected.Id);
        }

        if (selected is null)
        {
            ClearSelection(items, ref selectedItem);
            return;
        }

        SetSelection(items, selected, ref selectedItem);
    }

    private void ToggleLeftTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleMiddleView(ShellItemViewModel item) => SelectItem(item, allowToggle: false);

    private void ToggleRightTopView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleLeftBottomView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void ToggleRightBottomView(ShellItemViewModel item) => SelectItem(item, allowToggle: true);

    private void SelectItem(ShellItemViewModel item, bool allowToggle)
    {
        if (!_viewsById.TryGetValue(item.Id, out var packageView))
        {
            return;
        }

        var placement = packageView.Placement;
        var items = GetBar(placement).Items;
        ref var selectedItem = ref GetSelectedItemReference(placement);

        if (allowToggle && ReferenceEquals(selectedItem, item))
        {
            ClearSelection(items, ref selectedItem);
            SetSelectedViewId(placement, null);
            ApplyPanelContent(placement, null);
            NotifyLayoutStateChanged();
            PersistShellState();
            return;
        }

        SetSelection(items, item, ref selectedItem);
        SetSelectedViewId(placement, item.Id);
        ApplyPanelContent(placement, item.Id);
        NotifyLayoutStateChanged();
        PersistShellState();
    }

    private void ApplyPanelContent(RailPlacement placement, string? viewId)
    {
        var panel = GetPanel(placement);
        panel.Lines.Clear();

        if (string.IsNullOrWhiteSpace(viewId) || !_viewsById.TryGetValue(viewId, out var packageView))
        {
            ApplyEmptyPanelState(placement, panel);
            return;
        }

        panel.Title = packageView.Title.ToUpperInvariant();
        panel.Subtitle = $"{packageView.PackageDisplayName} · {packageView.PackageId} · v{packageView.PackageVersion}";
        panel.Summary = packageView.Readiness == PackageReadinessState.Ready
            ? $"{ToPlacementDisplay(placement).ToUpperInvariant()} PACKAGE ACTIVE"
            : $"PACKAGE {packageView.Readiness.ToString().ToUpperInvariant()}";
        AddCommonViewLines(panel.Lines, packageView);
        panel.HostedView = _packageViewHostService.GetOrCreateView(viewId);

        if (placement != RailPlacement.Middle)
        {
            return;
        }

        if (_startupWarnings.Count == 0 && _startupErrors.Count == 0)
        {
            panel.Lines.Add("Runtime composition completed without package load warnings.");
            return;
        }

        foreach (var warning in _startupWarnings)
        {
            panel.Lines.Add($"Warning: {warning}");
        }

        foreach (var error in _startupErrors)
        {
            panel.Lines.Add($"Error: {error}");
        }
    }

    private void ApplyEmptyPanelState(RailPlacement placement, ShellPanelViewModel panel)
    {
        panel.HostedView = null;

        if (placement == RailPlacement.Middle)
        {
            var hasMiddlePackages = MiddleBar.Items.Count > 0;
            panel.Title = "WELCOME";
            panel.Subtitle = hasMiddlePackages
                ? "Middle package views are available."
                : "No packages are currently assigned to the middle bar.";
            panel.Summary = hasMiddlePackages
                ? "Click a package icon in the middle bar to open it here."
                : "Install a package or move one into the middle bar to make this workspace active.";

            if (_startupErrors.Count == 0 && _startupWarnings.Count == 0)
            {
                panel.Lines.Add(hasMiddlePackages
                    ? "The middle workspace is ready. Select a package icon to reopen a view."
                    : "Load a dev package or move a package view into the middle bar to get started.");
            }
            else
            {
                foreach (var warning in _startupWarnings)
                {
                    panel.Lines.Add($"Warning: {warning}");
                }

                foreach (var error in _startupErrors)
                {
                    panel.Lines.Add($"Error: {error}");
                }
            }

            return;
        }

        panel.Title = ToPlacementDisplay(placement).ToUpperInvariant();
        panel.Subtitle = string.Empty;
        panel.Summary = $"Select a package icon to open the {ToPlacementDisplay(placement)} panel.";
        panel.Lines.Add($"No package is currently open in {ToPlacementDisplay(placement)}.");
    }

    private void SetOrderForPlacement(RailPlacement placement, IReadOnlyList<string> orderedViewIds)
    {
        var placementViewIds = _viewsById.Values
            .Where(view => view.Placement == placement)
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var viewId in placementViewIds)
        {
            _shellState.ViewOrder.Remove(viewId);
        }

        for (var index = 0; index < orderedViewIds.Count; index++)
        {
            _shellState.ViewOrder[orderedViewIds[index]] = index;
        }
    }

    private static void InsertAt(IList<string> orderedViewIds, string viewId, int? targetIndex)
    {
        var normalizedIndex = targetIndex.HasValue
            ? Math.Clamp(targetIndex.Value, 0, orderedViewIds.Count)
            : orderedViewIds.Count;

        orderedViewIds.Insert(normalizedIndex, viewId);
    }

    private void OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
    {
        if (!RemovePackageViewsFromShell(e.PackageId))
        {
            return;
        }

        RebuildRailCollections();
        PersistShellState();
    }

    private void ApplyActivePackagesToShell(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        var shellSnapshot = new ShellCompositionService().Compose(
            activePackages,
            _shellState,
            systemStatus: null,
            _startupWarnings,
            _startupErrors);

        _viewsById.Clear();
        foreach (var view in shellSnapshot.PackageViews)
        {
            _viewsById[view.ViewId] = view;
        }

        SyncStatusText = shellSnapshot.SyncStatusText;
        RebuildRailCollections();
        PersistShellState();
    }

    private bool RemovePackageViewsFromShell(string packageId)
    {
        var removedViewIds = _viewsById.Values
            .Where(view => string.Equals(view.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .Select(view => view.ViewId)
            .ToArray();

        if (removedViewIds.Length == 0)
        {
            return false;
        }

        foreach (var viewId in removedViewIds)
        {
            _viewsById.Remove(viewId);
            _shellState.ViewPlacements.Remove(viewId);
            _shellState.ViewOrder.Remove(viewId);
            _shellState.HiddenHotbarViewIds.Remove(viewId);

            if (string.Equals(_shellState.SelectedLeftTopViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                _shellState.SelectedLeftTopViewId = null;
            }

            if (string.Equals(_shellState.SelectedMiddleViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                _shellState.SelectedMiddleViewId = null;
            }

            if (string.Equals(_shellState.SelectedRightTopViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                _shellState.SelectedRightTopViewId = null;
            }

            if (string.Equals(_shellState.SelectedLeftBottomViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                _shellState.SelectedLeftBottomViewId = null;
            }

            if (string.Equals(_shellState.SelectedRightBottomViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                _shellState.SelectedRightBottomViewId = null;
            }
        }

        return true;
    }

    private void PersistShellState()
    {
        _shellState.LayoutVersion = ShellState.CurrentLayoutVersion;
        _shellState.HasInitializedLayout = true;
        _shellState.LeftPanelWidth = LeftPanelWidth;
        _shellState.RightPanelWidth = RightPanelWidth;
        _shellState.TopRowHeightRatio = TopRowHeightRatio;
        _shellState.BottomSplitRatio = BottomSplitRatio;
        QueueShellStateSave();
    }

    private void QueueShellStateSave()
    {
        if (_disposed)
        {
            return;
        }

        _pendingShellStateSaveCts?.Cancel();
        _pendingShellStateSaveCts?.Dispose();

        var cancellationTokenSource = new CancellationTokenSource();
        _pendingShellStateSaveCts = cancellationTokenSource;
        var snapshot = CreateShellStateSnapshot();
        _ = PersistShellStateAsync(snapshot, cancellationTokenSource.Token);
    }

    private async Task PersistShellStateAsync(ShellState snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ShellStateSaveDelayMilliseconds, cancellationToken);
            await _shellStateService.SaveAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SaveShellStateImmediately()
    {
        var snapshot = CreateShellStateSnapshot();
        _shellStateService.Save(snapshot);
    }

    private ShellState CreateShellStateSnapshot()
        => new()
        {
            LayoutVersion = ShellState.CurrentLayoutVersion,
            HasInitializedLayout = true,
            ViewPlacements = new Dictionary<string, RailPlacement>(_shellState.ViewPlacements, StringComparer.OrdinalIgnoreCase),
            ViewOrder = new Dictionary<string, int>(_shellState.ViewOrder, StringComparer.OrdinalIgnoreCase),
            HiddenHotbarViewIds = new HashSet<string>(_shellState.HiddenHotbarViewIds, StringComparer.OrdinalIgnoreCase),
            SelectedLeftTopViewId = _shellState.SelectedLeftTopViewId,
            SelectedMiddleViewId = _shellState.SelectedMiddleViewId,
            SelectedRightTopViewId = _shellState.SelectedRightTopViewId,
            SelectedLeftBottomViewId = _shellState.SelectedLeftBottomViewId,
            SelectedRightBottomViewId = _shellState.SelectedRightBottomViewId,
            LeftPanelWidth = LeftPanelWidth,
            RightPanelWidth = RightPanelWidth,
            TopRowHeightRatio = TopRowHeightRatio,
            BottomSplitRatio = BottomSplitRatio,
            ThemeId = _shellState.ThemeId,
            PreferredRuntimeUrl = _shellState.PreferredRuntimeUrl,
        };

    private ShellItemViewModel CreateShellItem(ShellPackageView packageView, Action<ShellItemViewModel> onSelect)
    {
        var tooltip = $"{packageView.PackageDisplayName} · {packageView.Title}";
        return new ShellItemViewModel(
            packageView.ViewId,
            packageView.Glyph,
            packageView.Title,
            packageView.PackageDisplayName,
            tooltip,
            packageView.Placement,
            onSelect);
    }

    private PackageIconBarViewModel GetBar(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => LeftTopBar,
            RailPlacement.Middle => MiddleBar,
            RailPlacement.RightTop => RightTopBar,
            RailPlacement.LeftBottom => LeftBottomBar,
            RailPlacement.RightBottom => RightBottomBar,
            _ => MiddleBar,
        };

    private ShellPanelViewModel GetPanel(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => LeftTopPanel,
            RailPlacement.Middle => MiddlePanel,
            RailPlacement.RightTop => RightTopPanel,
            RailPlacement.LeftBottom => LeftBottomPanel,
            RailPlacement.RightBottom => RightBottomPanel,
            _ => MiddlePanel,
        };

    private ref ShellItemViewModel? GetSelectedItemReference(RailPlacement placement)
    {
        switch (placement)
        {
            case RailPlacement.LeftTop:
                return ref _selectedLeftTopItem;
            case RailPlacement.Middle:
                return ref _selectedMiddleItem;
            case RailPlacement.RightTop:
                return ref _selectedRightTopItem;
            case RailPlacement.LeftBottom:
                return ref _selectedLeftBottomItem;
            case RailPlacement.RightBottom:
                return ref _selectedRightBottomItem;
            default:
                return ref _selectedMiddleItem;
        }
    }

    private void ClearSelectionForPlacement(RailPlacement placement, string viewId)
    {
        if (string.Equals(GetSelectedViewId(placement), viewId, StringComparison.OrdinalIgnoreCase))
        {
            SetSelectedViewId(placement, null);
        }
    }

    private string? GetSelectedViewId(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => _shellState.SelectedLeftTopViewId,
            RailPlacement.Middle => _shellState.SelectedMiddleViewId,
            RailPlacement.RightTop => _shellState.SelectedRightTopViewId,
            RailPlacement.LeftBottom => _shellState.SelectedLeftBottomViewId,
            RailPlacement.RightBottom => _shellState.SelectedRightBottomViewId,
            _ => _shellState.SelectedMiddleViewId,
        };

    private void SetSelectedViewId(RailPlacement placement, string? viewId)
    {
        switch (placement)
        {
            case RailPlacement.LeftTop:
                _shellState.SelectedLeftTopViewId = viewId;
                break;
            case RailPlacement.Middle:
                _shellState.SelectedMiddleViewId = viewId;
                break;
            case RailPlacement.RightTop:
                _shellState.SelectedRightTopViewId = viewId;
                break;
            case RailPlacement.LeftBottom:
                _shellState.SelectedLeftBottomViewId = viewId;
                break;
            case RailPlacement.RightBottom:
                _shellState.SelectedRightBottomViewId = viewId;
                break;
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
    }

    private static void AddCommonViewLines(ICollection<string> lines, ShellPackageView packageView)
    {
        lines.Add($"Package id: {packageView.PackageId}");
        lines.Add($"Version: {packageView.PackageVersion}");
        lines.Add($"Placement: {ToPlacementDisplay(packageView.Placement)}");
        lines.Add($"Readiness: {ToReadinessDisplay(packageView.Readiness)}");
    }

    private static string ToReadinessDisplay(PackageReadinessState readiness)
        => readiness switch
        {
            PackageReadinessState.Ready => "Ready",
            PackageReadinessState.NeedsConfiguration => "Needs configuration",
            PackageReadinessState.Degraded => "Degraded",
            PackageReadinessState.Failed => "Failed",
            _ => "Unknown",
        };

    private static string ToPlacementDisplay(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => "Left Top",
            RailPlacement.Middle => "Middle",
            RailPlacement.RightTop => "Right Top",
            RailPlacement.LeftBottom => "Left Bottom",
            RailPlacement.RightBottom => "Right Bottom",
            _ => placement.ToString(),
        };

    private static PackageHotbarPlacement ToPackageHotbarPlacement(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => PackageHotbarPlacement.LeftTop,
            RailPlacement.Middle => PackageHotbarPlacement.Middle,
            RailPlacement.RightTop => PackageHotbarPlacement.RightTop,
            RailPlacement.LeftBottom => PackageHotbarPlacement.LeftBottom,
            RailPlacement.RightBottom => PackageHotbarPlacement.RightBottom,
            _ => PackageHotbarPlacement.Middle,
        };

    private static RailPlacement ToRailPlacement(PackageHotbarPlacement placement)
        => placement switch
        {
            PackageHotbarPlacement.LeftTop => RailPlacement.LeftTop,
            PackageHotbarPlacement.Middle => RailPlacement.Middle,
            PackageHotbarPlacement.RightTop => RailPlacement.RightTop,
            PackageHotbarPlacement.LeftBottom => RailPlacement.LeftBottom,
            PackageHotbarPlacement.RightBottom => RailPlacement.RightBottom,
            _ => RailPlacement.Middle,
        };

    private static void SetSelection(
        IEnumerable<ShellItemViewModel> items,
        ShellItemViewModel selected,
        ref ShellItemViewModel? selectedItem)
    {
        foreach (var item in items)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }

        selectedItem = selected;
    }

    private static void ClearSelection(IEnumerable<ShellItemViewModel> items, ref ShellItemViewModel? selectedItem)
    {
        foreach (var item in items)
        {
            item.IsSelected = false;
        }

        selectedItem = null;
    }
}

public sealed class NotificationItemViewModel(AppNotificationRecord notification, DateTimeOffset lastReadAtUtc)
{
    public string NotificationId { get; } = notification.NotificationId;

    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public string SourceText { get; } = $"{(string.IsNullOrWhiteSpace(notification.SourceDisplayName) ? notification.SourcePackageId : notification.SourceDisplayName)} · {notification.CreatedAtUtc.ToLocalTime():g}";

    public bool IsUnread { get; } = notification.CreatedAtUtc > lastReadAtUtc;

    public string SeverityGlyph { get; } = ToSeverityGlyph(notification.Severity);

    public string SeverityText { get; } = ToSeverityText(notification.Severity);

    private static string ToSeverityGlyph(PackageNotificationSeverity severity)
        => severity switch
        {
            PackageNotificationSeverity.Success => "✓",
            PackageNotificationSeverity.Warning => "!",
            PackageNotificationSeverity.Error => "!",
            _ => "i",
        };

    private static string ToSeverityText(PackageNotificationSeverity severity)
        => severity switch
        {
            PackageNotificationSeverity.Success => "Success",
            PackageNotificationSeverity.Warning => "Warning",
            PackageNotificationSeverity.Error => "Error",
            _ => "Info",
        };
}

public sealed class ToastNotificationViewModel(AppToastNotification notification)
{
    public string NotificationId { get; } = notification.NotificationId;

    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public string SourceText { get; } = string.IsNullOrWhiteSpace(notification.SourceDisplayName)
        ? notification.SourcePackageId
        : notification.SourceDisplayName;

    public string SeverityGlyph { get; } = NotificationItemViewModelSeverity.ToGlyph(notification.Severity);
}

internal static class NotificationItemViewModelSeverity
{
    public static string ToGlyph(PackageNotificationSeverity severity)
        => severity switch
        {
            PackageNotificationSeverity.Success => "✓",
            PackageNotificationSeverity.Warning => "!",
            PackageNotificationSeverity.Error => "!",
            _ => "i",
        };
}
