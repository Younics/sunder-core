using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Composition;

public sealed class WindowLauncherFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    CliInstallationService cliInstallationService,
    NotificationCenterService notificationCenter,
    DeveloperLogService developerLog,
    ShellStateService shellStateService,
    ShellState shellState,
    SunderUpdateService updateService,
    BackgroundTaskQueueService backgroundTaskQueue,
    SettingsWindowFactory settingsWindowFactory,
    PackagesWindowFactory packagesWindowFactory,
    StacksWindowFactory stacksWindowFactory,
    IUiDispatcher uiDispatcher,
    AppPackageDeveloperServices? developerServices = null)
{
    public WindowLauncher Create(
        PackageViewHostService packageViewHostService,
        RuntimeEventSubscriptionService? runtimeEventSubscription)
        => new(
            packageViewHostService,
            runtimeApiClientFactory,
            cliInstallationService,
            notificationCenter,
            shellStateService,
            shellState,
            settingsWindowFactory,
            packagesWindowFactory,
            stacksWindowFactory,
            uiDispatcher,
            developerLog,
            updateService,
            backgroundTaskQueue,
            runtimeEventSubscription,
            developerServices);
}
