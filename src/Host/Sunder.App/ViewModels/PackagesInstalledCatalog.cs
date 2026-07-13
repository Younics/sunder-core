using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackagesInstalledCatalog(
    IRuntimePackagesClient runtimeApiClient,
    PackageRegistryClientProvider registryClientProvider)
{
    private readonly List<SessionPackageDescriptor> _sessionPackages = [];
    private readonly List<InstalledPackageDescriptor> _installedPackages = [];
    private readonly List<RegistryPackageUpdate> _availableUpdates = [];

    public IReadOnlyList<SessionPackageDescriptor> SessionPackages => _sessionPackages;

    public IReadOnlyList<InstalledPackageDescriptor> InstalledPackages => _installedPackages;

    public IReadOnlyList<RegistryPackageUpdate> AvailableUpdates => _availableUpdates;

    public bool IsEmpty => _installedPackages.Count == 0 && _sessionPackages.Count == 0;

    public int InstalledPackageCount => _installedPackages.Count;

    public int ActivePackageCount => _sessionPackages.Count(package => package.IsEnabled);

    public int DisabledPackageCount => _installedPackages.Count(package => !package.IsEnabled);

    public int FailedPackageCount => _sessionPackages.Count(package => package.Readiness == PackageReadinessState.Failed);

    public int AvailableUpdateCount => _availableUpdates.Count;

    public async Task RefreshAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        var sessionPackagesTask = runtimeApiClient.GetSessionPackagesAsync(cancellationToken);
        var installedPackagesTask = runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        await Task.WhenAll(sessionPackagesTask, installedPackagesTask).ConfigureAwait(false);

        ReplaceItems(_sessionPackages, await sessionPackagesTask.ConfigureAwait(false));
        ReplaceItems(_installedPackages, await installedPackagesTask.ConfigureAwait(false));
        await ResolveAvailableUpdatesAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshInstalledPackageStateOnlyAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        ReplaceItems(_installedPackages, await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false));
        await ResolveAvailableUpdatesAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.FirstOrDefault(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _availableUpdates.FirstOrDefault(update => string.Equals(update.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private async Task ResolveAvailableUpdatesAsync(Action<string> addWarning, CancellationToken cancellationToken)
    {
        _availableUpdates.Clear();
        if (_installedPackages.Count == 0 || !registryClientProvider.TryResolve(out var registryUrl, out _) || registryUrl is null)
        {
            return;
        }

        try
        {
            var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(
                new RuntimeRegistryPackageBatchRequest(
                    registryUrl.AbsoluteUri,
                    _installedPackages.Select(package => new RegistryPackageChangeRequest(package.PackageId, null, "latest")).ToArray()),
                cancellationToken).ConfigureAwait(false);
            _availableUpdates.AddRange(plan.Items
                .Where(item => item.CurrentVersion is not null && !string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
                .Select(item => new RegistryPackageUpdate(item.PackageId, item.CurrentVersion!, item.Version, item.DeprecatedMessage, item.Artifact)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            addWarning($"Registry update check failed: {ex.Message}");
        }
    }

    private static void ReplaceItems<T>(List<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        target.AddRange(items);
    }
}
