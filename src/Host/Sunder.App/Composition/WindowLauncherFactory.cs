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
    BackgroundProcessQueueService backgroundProcessQueue,
    SettingsWindowFactory settingsWindowFactory,
    PackagesWindowFactory packagesWindowFactory)
{
    public WindowLauncher Create(PackageViewHostService packageViewHostService)
        => new(
            packageViewHostService,
            runtimeApiClientFactory,
            cliInstallationService,
            notificationCenter,
            shellStateService,
            shellState,
            developerLog,
            updateService,
            backgroundProcessQueue,
            settingsWindowFactory,
            packagesWindowFactory);
}
