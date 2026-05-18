using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Protocol;

namespace Sunder.App.Composition;

public sealed class MainWindowFactory(
    ShellStateService shellStateService,
    RuntimeConnectionState runtimeConnectionState,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    RuntimeHostProcessManager runtimeHostProcessManager,
    NotificationCenterService notificationCenter,
    DeveloperLogService developerLog,
    AppPackageShellViewService shellViewService,
    SunderUpdateService updateService,
    BackgroundProcessQueueService backgroundProcessQueue,
    IShellCompositionService shellCompositionService)
{
    public (MainWindow Window, MainWindowViewModel ViewModel) Create(
        IWindowLauncher windowLauncher,
        ShellSnapshot shellSnapshot,
        PackageViewHostService packageViewHostService,
        SystemStatusResponse? initialSystemStatus,
        bool deferInitialHostedViews)
    {
        var viewModel = new MainWindowViewModel(
            windowLauncher,
            shellStateService,
            shellSnapshot,
            packageViewHostService,
            runtimeConnectionState,
            runtimeApiClientFactory,
            runtimeHostProcessManager,
            initialSystemStatus,
            notificationCenter,
            shellViewService,
            updateService,
            deferInitialHostedViews,
            backgroundProcessQueue,
            shellCompositionService: shellCompositionService,
            developerLog: developerLog);

        return (new MainWindow { DataContext = viewModel }, viewModel);
    }
}
