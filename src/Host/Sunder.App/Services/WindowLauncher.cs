using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.App.Composition;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Protocol;

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
    private readonly SettingsWindowFactory? _settingsWindowFactory;
    private readonly PackagesWindowFactory? _packagesWindowFactory;
    private readonly bool _ownsBackgroundProcessQueue;
    private SettingsWindow? _settingsWindow;
    private PackagesWindow? _packagesWindow;
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
        DeveloperLogService? developerLog = null,
        SunderUpdateService? updateService = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        SettingsWindowFactory? settingsWindowFactory = null,
        PackagesWindowFactory? packagesWindowFactory = null)
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
        _ownsBackgroundProcessQueue = backgroundProcessQueue is null;
        _backgroundProcessQueue = backgroundProcessQueue ?? new BackgroundProcessQueueService();
        _packageOperationService = new PackageOperationService(
            _backgroundProcessQueue,
            _runtimeApiClientFactory,
            ApplyPackageLifecycleChangesAsync,
            _notificationCenter);
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

    public void ShowSettings()
    {
        var createdWindow = _settingsWindow is null;
        _settingsWindow ??= CreateSettingsWindow();
        ShowWindow(_settingsWindow);
        if (!createdWindow)
        {
            _ = RefreshSettingsWindowPackageSectionsAsync();
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
        if (_ownsBackgroundProcessQueue)
        {
            _backgroundProcessQueue.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task CancelBackgroundProcessesAsync(CancellationToken cancellationToken = default)
        => await _backgroundProcessQueue.CancelAllAsync(cancellationToken);

    internal async Task<AppPackageHostStage> StagePackageLifecycleAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        CancellationToken cancellationToken = default)
        => await _packageViewHostService.StageForPackagesAsync(activePackages, packageSources, cancellationToken).ConfigureAwait(false);

    internal async Task CommitPackageLifecycleStageAsync(
        AppPackageHostStage stage,
        CancellationToken cancellationToken = default)
    {
        await _packageViewHostService.CommitStageAsync(stage, cancellationToken).ConfigureAwait(false);
        if (_mainWindowViewModel is not null)
        {
            await _mainWindowViewModel.ApplyPackageLifecycleSnapshotAsync(
                stage.ActivePackages,
                cancellationToken,
                deferHostedViewCreation: true).ConfigureAwait(false);
        }

        await RefreshSettingsWindowPackageSectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var window = _settingsWindowFactory?.Create(_packageViewHostService, PersistBackgroundProcessPopoverSize)
            ?? new SettingsWindow(_shellStateService, _shellState)
            {
                DataContext = new SettingsWindowViewModel(
                    _runtimeApiClientFactory.CreateClient(),
                    _packageViewHostService,
                    _cliInstallationService,
                    _updateService,
                    _backgroundProcessQueue,
                    _shellState.BackgroundProcessPopoverWidth,
                    _shellState.BackgroundProcessPopoverHeight,
                    PersistBackgroundProcessPopoverSize),
            };

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
        var window = _packagesWindowFactory?.Create(
            ApplyPackageLifecycleChangesAsync,
            _packageOperationService,
            PersistBackgroundProcessPopoverSize);
        if (window is null)
        {
            window = new PackagesWindow(_shellStateService, _shellState);
            window.DataContext = new PackagesWindowViewModel(
                _runtimeApiClientFactory.CreateClient(),
                new PackageArchivePicker(window),
                ApplyPackageLifecycleChangesAsync,
                _packageOperationService,
                _backgroundProcessQueue,
                notificationCenter: _notificationCenter,
                backgroundProcessPopoverWidth: _shellState.BackgroundProcessPopoverWidth,
                backgroundProcessPopoverHeight: _shellState.BackgroundProcessPopoverHeight,
                persistBackgroundProcessPopoverSize: PersistBackgroundProcessPopoverSize);
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_packagesWindow, window))
            {
                _packagesWindow = null;
            }
        };

        return window;
    }

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
        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        window.Show();
        window.Activate();
    }

    private void PersistBackgroundProcessPopoverSize(double width, double height)
    {
        _shellState.BackgroundProcessPopoverWidth = width;
        _shellState.BackgroundProcessPopoverHeight = height;
        _shellStateService.Save(_shellState);
    }

    private async Task ApplyPackageLifecycleChangesAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mainWindowViewModel is not null)
        {
            await _mainWindowViewModel.ApplyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken, deferHostedViewCreation: true).ConfigureAwait(false);
        }

        await RefreshSettingsWindowPackageSectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSettingsWindowPackageSectionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Dispatcher.UIThread.CheckAccess() && Application.Current is not null)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await RefreshSettingsWindowPackageSectionsAsync(cancellationToken);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            await completion.Task.ConfigureAwait(false);
            return;
        }

        if (_settingsWindow?.DataContext is SettingsWindowViewModel viewModel)
        {
            await viewModel.RefreshPackageSectionsAsync(cancellationToken);
        }
    }
}
