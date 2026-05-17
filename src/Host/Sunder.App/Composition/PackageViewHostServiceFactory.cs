using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.Composition;

public sealed class PackageViewHostServiceFactory(
    PackageRuntimeFaultReporter packageFaultReporter,
    AppPackageShellViewService shellViewService,
    AppPackageSettingsNavigationService settingsNavigationService,
    NotificationCenterService notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue)
{
    public async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        CancellationToken cancellationToken = default)
        => await PackageViewHostService.CreateForPackagesAsync(
            activePackages,
            packageSources,
            packageFaultReporter,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            cancellationToken).ConfigureAwait(false);
}
