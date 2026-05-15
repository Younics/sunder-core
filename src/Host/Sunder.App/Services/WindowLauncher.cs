using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Services;

public sealed class WindowLauncher : IWindowLauncher
{
    private readonly PackageViewHostService _packageViewHostService;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly CliInstallationService _cliInstallationService;
    private readonly NotificationCenterService _notificationCenter;
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _shellState;
    private readonly SunderUpdateService _updateService;
    private readonly BackgroundProcessQueueService _backgroundProcessQueue;
    private readonly PackageOperationService _packageOperationService;
    private SettingsWindow? _settingsWindow;
    private PackagesWindow? _packagesWindow;
    private MainWindowViewModel? _mainWindowViewModel;

    public WindowLauncher(
        PackageViewHostService packageViewHostService,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        CliInstallationService cliInstallationService,
        NotificationCenterService notificationCenter,
        ShellStateService shellStateService,
        ShellState shellState,
        SunderUpdateService? updateService = null)
    {
        _packageViewHostService = packageViewHostService;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _cliInstallationService = cliInstallationService;
        _notificationCenter = notificationCenter;
        _shellStateService = shellStateService;
        _shellState = shellState;
        _updateService = updateService ?? new SunderUpdateService();
        _backgroundProcessQueue = new BackgroundProcessQueueService();
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

    public void CloseForShutdown()
    {
        _ = _packageOperationService.CancelAllAsync();

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

    }

    public async Task CancelBackgroundProcessesAsync(CancellationToken cancellationToken = default)
        => await _packageOperationService.CancelAllAsync(cancellationToken);

    private SettingsWindow CreateSettingsWindow()
    {
        var window = new SettingsWindow(_shellStateService, _shellState)
        {
            DataContext = new SettingsWindowViewModel(
                _runtimeApiClientFactory.CreateClient(),
                _packageViewHostService,
                _cliInstallationService,
                _updateService),
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
        var window = new PackagesWindow(_shellStateService, _shellState);
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

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_packagesWindow, window))
            {
                _packagesWindow = null;
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await ApplyPackageLifecycleChangesOnUiThreadAsync(impactedPackageIds, cancellationToken));
            return;
        }

        await ApplyPackageLifecycleChangesOnUiThreadAsync(impactedPackageIds, cancellationToken);
    }

    private async Task ApplyPackageLifecycleChangesOnUiThreadAsync(
        IReadOnlyList<string> impactedPackageIds,
        CancellationToken cancellationToken)
    {
        if (_mainWindowViewModel is not null)
        {
            await _mainWindowViewModel.ApplyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken, deferHostedViewCreation: true);
        }

        await RefreshSettingsWindowPackageSectionsAsync(cancellationToken);
    }

    private async Task RefreshSettingsWindowPackageSectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsWindow?.DataContext is SettingsWindowViewModel viewModel)
        {
            await viewModel.RefreshPackageSectionsAsync(cancellationToken);
        }
    }
}
