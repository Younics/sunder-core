using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class MainWindowFactory(
    ShellStateService shellStateService,
    RuntimeConnectionState runtimeConnectionState,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    RuntimeAvailabilityManager runtimeAvailabilityManager,
    NotificationCenterService notificationCenter,
    DeveloperLogService developerLog,
    AppPackageShellViewService shellViewService,
    SunderUpdateService updateService,
    BackgroundTaskQueueService backgroundTaskQueue,
    RegistryAuthService registryAuthService,
    ExternalBrowserService externalBrowserService,
    RemoteImageService remoteImageService,
    IShellCompositionService shellCompositionService,
    IUiDispatcher uiDispatcher)
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
            runtimeAvailabilityManager,
            initialSystemStatus,
            notificationCenter,
            shellViewService,
            updateService,
            deferInitialHostedViews,
            backgroundTaskQueue,
            shellCompositionService: shellCompositionService,
            developerLog: developerLog,
            registryAuthService: registryAuthService,
            externalBrowserService: externalBrowserService,
            remoteImageService: remoteImageService,
            uiDispatcher: uiDispatcher);

        var window = new MainWindow { DataContext = viewModel };
        window.ConfigureState(shellStateService, shellSnapshot.State);
        viewModel.ConfigurePackageViewStagingSurface(
            window.StagePackageView,
            window.DetachStagedPackageViews,
            window.AcquirePackageViewTransitionSnapshot);
        return (window, viewModel);
    }
}
