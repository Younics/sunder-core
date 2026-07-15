using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class PackageViewHostServiceFactory(
    PackageRuntimeFaultReporter packageFaultReporter,
    AppPackageShellViewService shellViewService,
    AppPackageSettingsNavigationService settingsNavigationService,
    AppPackageSessionService packageSessionService,
    RuntimeClientTransport runtimeTransport,
    NotificationCenterService notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue,
    AppPackageResourceAssemblyRegistry packageResourceAssemblyRegistry,
    IUiDispatcher uiDispatcher)
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
            runtimeTransport.GetConnectionInfo,
            uiDispatcher,
            cancellationToken).ConfigureAwait(false);
}
