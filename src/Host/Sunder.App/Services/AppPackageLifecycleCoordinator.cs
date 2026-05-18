using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class AppPackageLifecycleCoordinator(
    PackageViewHostService packageViewHostService,
    IRuntimeApiClientFactory runtimeApiClientFactory)
{
    public async Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageDeltaFromRuntimeAsync(
        IReadOnlyCollection<string>? impactedPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
        var activePackagesTask = runtimeApiClient.GetActivePackagesAsync(cancellationToken);
        var packageSourcesTask = runtimeApiClient.GetActivePackageSourcesAsync(cancellationToken);
        await Task.WhenAll(activePackagesTask, packageSourcesTask).ConfigureAwait(false);

        var activePackages = await activePackagesTask.ConfigureAwait(false);
        var packageSources = await packageSourcesTask.ConfigureAwait(false);
        var impactedPackages = impactedPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(impactedPackageIds, StringComparer.OrdinalIgnoreCase);

        await packageViewHostService.ApplyPackageDeltaAsync(
            activePackages,
            packageSources,
            impactedPackages,
            cancellationToken).ConfigureAwait(false);

        return packageViewHostService.FilterEnabledPackages(activePackages);
    }
}
