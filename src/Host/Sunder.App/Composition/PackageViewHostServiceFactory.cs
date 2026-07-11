using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class PackageViewHostServiceFactory(
    PackageRuntimeFaultReporter packageFaultReporter,
    AppPackageShellViewService shellViewService,
    AppPackageSettingsNavigationService settingsNavigationService,
    AppPackageSessionService packageSessionService,
    RuntimeConnectionState runtimeConnectionState,
    NotificationCenterService notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue,
    AppPackageResourceAssemblyRegistry packageResourceAssemblyRegistry)
{
    public async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
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
            () => runtimeConnectionState.ConnectionInfo,
            cancellationToken).ConfigureAwait(false);
}
