using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Sunder.App.Composition;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class WindowLauncher : IWindowLauncher, IDisposable
{
    private readonly PackageViewHostService _packageViewHostService;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly CliInstallationService _cliInstallationService;
    private readonly NotificationCenterService _notificationCenter;
    private readonly DeveloperLogService _developerLog;
    private readonly AppPackageDeveloperServices? _developerServices;
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _shellState;
    private readonly SunderUpdateService _updateService;
    private readonly BackgroundTaskQueueService _backgroundTaskQueue;
    private readonly PackageOperationService _packageOperationService;
    private readonly RegistryPackageInstallService _registryPackageInstallService;
    private readonly Func<RuntimePackageStamp, CancellationToken, Task> _waitUntilPackagePresentationAppliedAsync;
    private readonly Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>> _waitForPackagePresentationAsync;
    private readonly IRuntimeExecutionRuntimeStatusSource? _runtimeStatusSource;
    private readonly SettingsWindowFactory _settingsWindowFactory;
    private readonly PackagesWindowFactory _packagesWindowFactory;
    private readonly StacksWindowFactory _stacksWindowFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(WindowLauncher));
    private readonly Dictionary<string, Task> _retirementReconciliations = new(StringComparer.Ordinal);
    private readonly bool _ownsBackgroundTaskQueue;
    private SettingsWindow? _settingsWindow;
    private PackagesWindow? _packagesWindow;
    private StacksWindow? _stacksWindow;
    private DeveloperLogWindow? _developerLogWindow;
    private MainWindowViewModel? _mainWindowViewModel;
    private bool _disposed;

    public WindowLauncher(
        PackageViewHostService packageViewHostService,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        CliInstallationService cliInstallationService,
        NotificationCenterService notificationCenter,
        ShellStateService shellStateService,
        ShellState shellState,
        SettingsWindowFactory settingsWindowFactory,
        PackagesWindowFactory packagesWindowFactory,
        StacksWindowFactory stacksWindowFactory,
        IUiDispatcher? uiDispatcher = null,
        DeveloperLogService? developerLog = null,
        SunderUpdateService? updateService = null,
        BackgroundTaskQueueService? backgroundTaskQueue = null,
        RuntimeEventSubscriptionService? runtimeEventSubscription = null,
        AppPackageDeveloperServices? developerServices = null)
    {
        _packageViewHostService = packageViewHostService;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _cliInstallationService = cliInstallationService;
        _notificationCenter = notificationCenter;
        _developerLog = developerLog ?? new DeveloperLogService();
        _developerServices = developerServices;
        _shellStateService = shellStateService;
        _shellState = shellState;
        _updateService = updateService ?? new SunderUpdateService();
        _settingsWindowFactory = settingsWindowFactory;
        _packagesWindowFactory = packagesWindowFactory;
        _stacksWindowFactory = stacksWindowFactory;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _ownsBackgroundTaskQueue = backgroundTaskQueue is null;
        _backgroundTaskQueue = backgroundTaskQueue ?? new BackgroundTaskQueueService();
        _runtimeStatusSource = runtimeEventSubscription;
        _waitUntilPackagePresentationAppliedAsync = runtimeEventSubscription is null
            ? WaitForUnavailablePresentationAsync
            : runtimeEventSubscription.WaitUntilAppliedAsync;
        _waitForPackagePresentationAsync = runtimeEventSubscription is null
            ? static (_, cancellationToken) => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<PackagePresentationResult>(cancellationToken)
                : Task.FromResult(PackagePresentationResult.Unavailable(
                    "Sunder is running in the Core Shell without a live Runtime presentation."))
            : runtimeEventSubscription.WaitForPresentationAsync;
        _registryPackageInstallService = new RegistryPackageInstallService(ConfirmPackageChangeAsync, _backgroundTaskQueue,
            error => _notificationCenter.PublishAsync("sunder.app", "Sunder",
                new Sunder.Sdk.Notifications.PackageNotificationRequest("Installation failed", error.Message,
                    Sunder.Sdk.Notifications.PackageNotificationDisplayMode.ToastAndTray,
                    Sunder.Sdk.Notifications.PackageNotificationSeverity.Error)).AsTask());
        _packageOperationService = new PackageOperationService(
            _backgroundTaskQueue,
            _runtimeApiClientFactory,
            _waitUntilPackagePresentationAppliedAsync,
            _notificationCenter,
            registryInstallService: _registryPackageInstallService,
            waitForPresentationAsync: _waitForPackagePresentationAsync,
            confirmLocalPackage: (review, token) => _uiDispatcher.InvokeAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                var dialog = new LocalPackageInstallConsentWindow(review);
                var result = dialog.ShowDialog<bool>(GetPackageDialogOwner());
                using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(false)));
                return await result.WaitAsync(token);
            }));
        _developerServices?.Attach(this, packageViewHostService);
    }

    public void AttachShell(MainWindowViewModel viewModel)
        => _mainWindowViewModel = viewModel;

    public BackgroundTaskQueueService BackgroundTasks => _backgroundTaskQueue;

    public void DetachShell(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_mainWindowViewModel, viewModel))
        {
            _mainWindowViewModel = null;
        }
    }

    public void ShowSettings(IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var createdWindow = _settingsWindow is null;
        _settingsWindow ??= CreateSettingsWindow();
        ShowWindow(_settingsWindow);
        if (parameters?.GetValueOrDefault("section") is { } section
            && _settingsWindow.DataContext is SettingsWindowViewModel settings)
            _tasks.Observe(settings.SelectCoreSectionAsync(section, _tasks.Token), "selecting Settings section");
        if (!createdWindow)
        {
            _tasks.Observe(RefreshSettingsWindowPackageSectionsAsync(_tasks.Token), "refreshing Settings package sections");
        }
    }

    public async Task<bool> ShowPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        _settingsWindow ??= CreateSettingsWindow();
        ShowWindow(_settingsWindow);
        return _settingsWindow.DataContext is SettingsWindowViewModel viewModel
               && await viewModel.SelectPackageSettingsAsync(packageId, parameters, cancellationToken);
    }

    public void ShowPackages()
    {
        _packagesWindow ??= CreatePackagesWindow();
        ShowWindow(_packagesWindow);
    }

    public void ShowStacks()
    {
        _stacksWindow ??= CreateStacksWindow();
        ShowWindow(_stacksWindow);
    }

    public async Task<bool> HandleLaunchRequestAsync(AppLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Kind)
        {
            case AppLaunchRequestKind.None:
                ActivateMainWindow();
                return true;
            case AppLaunchRequestKind.PackageDetails:
            case AppLaunchRequestKind.PackageInstall:
                _packagesWindow ??= CreatePackagesWindow();
                ShowWindow(_packagesWindow);
                if (_packagesWindow.DataContext is PackagesWindowViewModel packagesViewModel)
                {
                    await packagesViewModel.ApplyLaunchRequestAsync(request, cancellationToken);
                }

                return true;
            case AppLaunchRequestKind.StackDetails:
            case AppLaunchRequestKind.StackUse:
            case AppLaunchRequestKind.StackFile:
                _stacksWindow ??= CreateStacksWindow();
                ShowWindow(_stacksWindow);
                if (_stacksWindow.DataContext is StacksWindowViewModel viewModel)
                {
                    await viewModel.ApplyLaunchRequestAsync(request, cancellationToken);
                }

                return true;
            case AppLaunchRequestKind.Invalid:
                await _notificationCenter.PublishAsync(
                    "sunder.app",
                    "Sunder",
                    new Sunder.Sdk.Notifications.PackageNotificationRequest(
                        "Sunder link could not be opened",
                        request.ErrorMessage ?? "The launch request is invalid.",
                        Sunder.Sdk.Notifications.PackageNotificationDisplayMode.ToastAndTray,
                        Sunder.Sdk.Notifications.PackageNotificationSeverity.Warning));
                return false;
            default:
                return false;
        }
    }

    public void ShowDeveloperLogs()
    {
        _developerLog.Enable();
        _developerLogWindow ??= CreateDeveloperLogWindow();
        ShowWindow(_developerLogWindow);
    }

    internal void ShowPackageDeveloperLogs(string packageId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        _developerLog.Enable();
        _developerLogWindow ??= CreateDeveloperLogWindow();
        ((DeveloperLogWindowViewModel)_developerLogWindow.DataContext!).SelectPackage(packageId);
        ShowWindow(_developerLogWindow);
    }

    internal async Task OpenDevelopmentViewAsync(string packageId, string viewId, Sunder.Sdk.Abstractions.PackageDevelopmentRevision revision, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (!_packageViewHostService.GetDevelopmentViews(packageId, revision).Any(view => view.ViewId == viewId))
            throw new InvalidOperationException("The view is not registered by this development package.");
        if (_mainWindowViewModel is not { } model)
            throw new InvalidOperationException("The App shell is unavailable.");
        ActivateMainWindow();
        if (!await model.OpenPackageViewPanelAsync(viewId))
            throw new InvalidOperationException("The package view could not be opened.");
        token.ThrowIfCancellationRequested();
        _ = _packageViewHostService.OpenDevelopmentView(packageId, viewId, revision);
    }

    internal string GetPackageAppReadiness(string packageId, RuntimePackageStamp stamp, string activationFingerprint,
        string? sourceContentIdentity = null)
        => _packageViewHostService.GetAppReadiness(packageId, stamp, activationFingerprint, sourceContentIdentity);

    internal async Task<bool> ConfirmDevelopmentRuntimesAsync(string packageId, RuntimePackageRuntimePrerequisitePlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var installations = plan.Requirements.Where(item => !item.Satisfied).Select(item => item.Installation!)
            .DistinctBy(item => item.Resolution!.Artifact.Identity).ToArray();
        var dialog = new RuntimeConfirmationWindow(new RuntimeConfirmationViewModel(
            "Prepare development package", $"Install the shared runtimes required by {packageId} before loading its development folder?",
            "Download & Install Runtimes", installations.SelectMany(item => RuntimeConfirmationViewModel.Install(item).Items).ToArray(),
            $"Device: {plan.Rid}\nSource: {plan.SourceFingerprint}\nThe development project is loaded directly from its built folder."));
        var result = dialog.ShowDialog<bool>(GetPackageDialogOwner());
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await result.WaitAsync(token);
    }

    public void CloseForShutdown()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.CloseForShutdown();
            _settingsWindow = null;
        }

        if (_packagesWindow is not null)
        {
            _packagesWindow.CloseForShutdown();
            _packagesWindow = null;
        }

        if (_stacksWindow is not null)
        {
            _stacksWindow.CloseForShutdown();
            _stacksWindow = null;
        }

        if (_developerLogWindow is not null)
        {
            _developerLogWindow.CloseForShutdown();
            _developerLogWindow = null;
        }

        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _developerServices?.Detach(this);
        _packageOperationService.Dispose();
        _registryPackageInstallService.Dispose();
        _tasks.Dispose();
        if (_ownsBackgroundTaskQueue)
        {
            _backgroundTaskQueue.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task CancelBackgroundTasksAsync(CancellationToken cancellationToken = default)
        => await _backgroundTaskQueue.CancelAllAsync(cancellationToken);

    private SettingsWindow CreateSettingsWindow()
    {
        var window = _settingsWindowFactory.Create(_packageViewHostService, PersistBackgroundTaskPopoverSize,
            _waitForPackagePresentationAsync, _runtimeStatusSource);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        };

        return window;
    }

    private PackagesWindow CreatePackagesWindow()
    {
        var window = _packagesWindowFactory.Create(
            _packageOperationService,
            PersistBackgroundTaskPopoverSize);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_packagesWindow, window))
            {
                _packagesWindow = null;
            }
        };

        return window;
    }

    private StacksWindow CreateStacksWindow()
    {
        var window = _stacksWindowFactory.Create(
            _waitUntilPackagePresentationAppliedAsync,
            _registryPackageInstallService);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_stacksWindow, window))
            {
                _stacksWindow = null;
            }
        };

        return window;
    }

    private Task<bool> ConfirmPackageChangeAsync(
        RegistryPackageChangePreflight preflight,
        CancellationToken cancellationToken)
        => _uiDispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = (Window?)_packagesWindow
                        ?? _stacksWindow
                        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is null)
            {
                return false;
            }
            var dialog = new PackageCapabilityConsentWindow(
                new PackageCapabilityConsentViewModel(preflight));
            var result = dialog.ShowDialog<bool>(owner);
            using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(false)));
            return await result.WaitAsync(cancellationToken);
        }).WaitAsync(cancellationToken);

    private Window GetPackageDialogOwner()
        => (_packagesWindow is { IsVisible: true } ? (Window)_packagesWindow : null)
            ?? (_stacksWindow is { IsVisible: true } ? _stacksWindow : null)
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
            ?? throw new InvalidOperationException("Open Packages to review this installation.");

    private DeveloperLogWindow CreateDeveloperLogWindow()
    {
        var window = new DeveloperLogWindow
        {
            DataContext = new DeveloperLogWindowViewModel(_developerLog),
        };

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_developerLogWindow, window))
            {
                _developerLogWindow = null;
            }
        };

        return window;
    }

    private static void ShowWindow(Window window)
    {
        WindowCloseToHideCoordinator.PrepareToShow(window);
        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        window.Show();
        window.Activate();
    }

    internal void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            ShowWindow(mainWindow);
        }
    }

    private void PersistBackgroundTaskPopoverSize(double width, double height)
    {
        _shellState.BackgroundTaskPopoverWidth = width;
        _shellState.BackgroundTaskPopoverHeight = height;
        _shellStateService.Update(_shellState, state =>
        {
            state.BackgroundTaskPopoverWidth = width;
            state.BackgroundTaskPopoverHeight = height;
        });
    }

    internal async Task ApplyPackageLifecycleSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mainWindowViewModel is not null)
        {
            await _mainWindowViewModel.ApplyPackageLifecycleSnapshotAsync(
                snapshot,
                packageSources,
                retryDisabledPackageIds,
                cancellationToken,
                detachAuxiliaryPackageViews: DetachSettingsWindowPackageView,
                refreshAuxiliaryPackageViewsAsync: operationCancellation =>
                    ApplyAuxiliaryWindowPackageSnapshotAsync(
                        snapshot,
                        operationCancellation)).ConfigureAwait(false);
            return;
        }

        await ApplyAuxiliaryWindowPackageSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    internal Task ReconcileDevelopmentReleaseAsync(RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> sources, CancellationToken token)
    {
        if (!CanReconcileDevelopment) return Task.CompletedTask;
        if (AppPackageLoadAdmission.IsCleaningActivation)
        {
            // Retirement can be awaited by presentation's own lifecycle gate. Queue
            // this explicit reconciliation with the shell owner rather than re-enter
            // that gate from a package's disposer. No cleanup authority flows to UI.
            if (ExecutionContext.IsFlowSuppressed()) Schedule();
            else { using (ExecutionContext.SuppressFlow()) Schedule(); }
            return Task.CompletedTask;
            void Schedule() => _tasks.Run(Reconcile,
                "reconciling an acknowledged development-owner release");
        }
        return Reconcile(token);

        Task Reconcile(CancellationToken cancellation) => _uiDispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            return CanReconcileDevelopment
                ? ApplyPackageLifecycleSnapshotAsync(snapshot, sources, null, cancellation)
                : Task.CompletedTask;
        });
    }

    internal bool CanReconcileDevelopment => !Volatile.Read(ref _disposed) && !_packageViewHostService.IsStopping;

    internal Task QueueRetirementReconciliation(string stageId, Func<CancellationToken, Task> reconcile)
    {
        lock (_retirementReconciliations)
        {
            if (!CanReconcileDevelopment) return Task.CompletedTask;
            if (_retirementReconciliations.TryGetValue(stageId, out var existing)) return existing;
            // Keep completed stage identities too: a delayed heartbeat must not
            // rebuild the same presentation again. No package context owns this task.
            Task queued;
            if (ExecutionContext.IsFlowSuppressed()) queued = Schedule();
            else { using (ExecutionContext.SuppressFlow()) queued = Schedule(); }
            _retirementReconciliations.Add(stageId, queued);
            return queued;
        }

        Task Schedule() => _tasks.RunTracked(async cancellation =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (CanReconcileDevelopment) await reconcile(cancellation).ConfigureAwait(false);
        }, $"reconciling App retirement for package stage '{stageId}'");
    }

    internal async Task ApplyPackageMetadataSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mainWindowViewModel is not null)
        {
            await _mainWindowViewModel.ApplyPackageMetadataSnapshotAsync(
                    snapshot,
                    cancellationToken,
                    operationCancellation => ApplyAuxiliaryWindowPackageSnapshotAsync(
                        snapshot,
                        operationCancellation))
                .ConfigureAwait(false);
            return;
        }
        await ApplyAuxiliaryWindowPackageSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private Task ApplyAuxiliaryWindowPackageSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        CancellationToken cancellationToken)
        => InvokeOnUiThreadAsync(async () =>
        {
            if (_packagesWindow?.DataContext is PackagesWindowViewModel packagesViewModel)
            {
                await packagesViewModel.ApplyPackageSnapshotAsync(snapshot, cancellationToken);
            }

            if (_settingsWindow?.DataContext is SettingsWindowViewModel settingsViewModel)
            {
                await settingsViewModel.RefreshPackageSectionsAsync(cancellationToken);
            }
        }, cancellationToken);

    private Task RefreshSettingsWindowPackageSectionsAsync(CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(async () =>
        {
            if (_settingsWindow?.DataContext is SettingsWindowViewModel viewModel)
            {
                await viewModel.RefreshPackageSectionsAsync(cancellationToken);
            }
        }, cancellationToken);

    private Task InvokeOnUiThreadAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_uiDispatcher.CheckAccess())
        {
            return action();
        }

        return _uiDispatcher.InvokeAsync<Task>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            },
            cancellationToken).Unwrap();
    }

    private void DetachSettingsWindowPackageView()
    {
        if (_settingsWindow?.DataContext is SettingsWindowViewModel viewModel)
        {
            viewModel.DetachHostedPackageSettingsView(preserveRetainedActivation: true);
        }
    }

    private static Task WaitForUnavailablePresentationAsync(
        RuntimePackageStamp stamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new InvalidOperationException(
            "Sunder is running in the Core Shell without a live Runtime presentation."));
    }
}
