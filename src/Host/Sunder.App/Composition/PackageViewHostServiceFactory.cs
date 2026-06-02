using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.Composition;

public sealed class PackageViewHostServiceFactory(
    PackageRuntimeFaultReporter packageFaultReporter,
    AppPackageShellViewService shellViewService,
    AppPackageSettingsNavigationService settingsNavigationService,
    AppPackageSessionService packageSessionService,
    NotificationCenterService notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue,
    AppPackageResourceAssemblyRegistry packageResourceAssemblyRegistry)
{
    public async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        CancellationToken cancellationToken = default)
        => await PackageViewHostService.CreateForPackagesWithResourceRegistryAsync(
            activePackages,
            packageSources,
            packageFaultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            packageResourceAssemblyRegistry,
            cancellationToken).ConfigureAwait(false);
}
