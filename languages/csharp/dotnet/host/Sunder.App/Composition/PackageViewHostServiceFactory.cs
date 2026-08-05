using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class PackageViewHostServiceFactory(
    AppPackageShellViewService shellViewService,
    AppPackageSettingsNavigationService settingsNavigationService,
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
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            packageResourceAssemblyRegistry,
            runtimeTransport.GetConnectionInfo,
            uiDispatcher,
            cancellationToken).ConfigureAwait(false);
}
