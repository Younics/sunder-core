using Avalonia.Controls;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Services;

public sealed class WindowLauncher(
    PackageViewHostService packageViewHostService,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    CliInstallationService cliInstallationService,
    NotificationCenterService notificationCenter,
    ShellStateService shellStateService,
    ShellState shellState,
    SunderUpdateService? updateService = null) : IWindowLauncher
{
    private readonly PackageViewHostService _packageViewHostService = packageViewHostService;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory = runtimeApiClientFactory;
    private readonly CliInstallationService _cliInstallationService = cliInstallationService;
    private readonly NotificationCenterService _notificationCenter = notificationCenter;
    private readonly ShellStateService _shellStateService = shellStateService;
    private readonly ShellState _shellState = shellState;
    private readonly SunderUpdateService _updateService = updateService ?? new SunderUpdateService();
    private SettingsWindow? _settingsWindow;
    private PackagesWindow? _packagesWindow;
    private MainWindowViewModel? _mainWindowViewModel;

    public void AttachShell(MainWindowViewModel viewModel)
        => _mainWindowViewModel = viewModel;

    public void DetachShell(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_mainWindowViewModel, viewModel))
        {
            _mainWindowViewModel = null;
        }
    }

    public void ShowSettings()
    {
        _settingsWindow ??= CreateSettingsWindow();
        ShowWindow(_settingsWindow);
    }

    public void ShowPackages()
    {
        _packagesWindow ??= CreatePackagesWindow();
        ShowWindow(_packagesWindow);
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

    }

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
            (impactedPackageIds, cancellationToken) => _mainWindowViewModel?.ApplyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken) ?? Task.CompletedTask,
            notificationCenter: _notificationCenter);

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
}
