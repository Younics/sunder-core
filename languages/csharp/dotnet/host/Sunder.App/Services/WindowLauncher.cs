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
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _shellState;
    private readonly SunderUpdateService _updateService;
    private readonly BackgroundProcessQueueService _backgroundProcessQueue;
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
    private readonly bool _ownsBackgroundProcessQueue;
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
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        RuntimeEventSubscriptionService? runtimeEventSubscription = null)
    {
        _packageViewHostService = packageViewHostService;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _cliInstallationService = cliInstallationService;
        _notificationCenter = notificationCenter;
        _developerLog = developerLog ?? new DeveloperLogService();
        _shellStateService = shellStateService;
        _shellState = shellState;
        _updateService = updateService ?? new SunderUpdateService();
        _settingsWindowFactory = settingsWindowFactory;
        _packagesWindowFactory = packagesWindowFactory;
        _stacksWindowFactory = stacksWindowFactory;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _ownsBackgroundProcessQueue = backgroundProcessQueue is null;
        _backgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
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
        _registryPackageInstallService = new RegistryPackageInstallService(ConfirmPackageChangeAsync, _backgroundProcessQueue,
            error => _notificationCenter.PublishAsync("sunder.app", "Sunder",
                new Sunder.Sdk.Notifications.PackageNotificationRequest("Installation failed", error.Message,
                    Sunder.Sdk.Notifications.PackageNotificationDisplayMode.ToastAndTray,
                    Sunder.Sdk.Notifications.PackageNotificationSeverity.Error)).AsTask());
        _packageOperationService = new PackageOperationService(
            _backgroundProcessQueue,
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
    }

    public void AttachShell(MainWindowViewModel viewModel)
        => _mainWindowViewModel = viewModel;

    public BackgroundProcessQueueService BackgroundProcesses => _backgroundProcessQueue;

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
        if (!_developerLog.IsEnabled)
        {
            return;
        }

        _developerLogWindow ??= CreateDeveloperLogWindow();
        ShowWindow(_developerLogWindow);
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
        _packageOperationService.Dispose();
        _registryPackageInstallService.Dispose();
        _tasks.Dispose();
        if (_ownsBackgroundProcessQueue)
        {
            _backgroundProcessQueue.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task CancelBackgroundProcessesAsync(CancellationToken cancellationToken = default)
        => await _backgroundProcessQueue.CancelAllAsync(cancellationToken);

    private SettingsWindow CreateSettingsWindow()
    {
        var window = _settingsWindowFactory.Create(_packageViewHostService, PersistBackgroundProcessPopoverSize,
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
            PersistBackgroundProcessPopoverSize);

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

    private void PersistBackgroundProcessPopoverSize(double width, double height)
    {
        _shellState.BackgroundProcessPopoverWidth = width;
        _shellState.BackgroundProcessPopoverHeight = height;
        _shellStateService.Update(_shellState, state =>
        {
            state.BackgroundProcessPopoverWidth = width;
            state.BackgroundProcessPopoverHeight = height;
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
            viewModel.DetachHostedPackageSettingsView();
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
