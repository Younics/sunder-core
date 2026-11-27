using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackagesInstalledCatalog(
    IRuntimePackagesClient runtimeApiClient, PackageUpdateStartupCheckService? updateChecks)
{
    private readonly List<SessionPackageDescriptor> _sessionPackages = [];
    private readonly List<InstalledPackageDescriptor> _installedPackages = [];
    private readonly List<RegistryPackageUpdate> _availableUpdates = [];
    private IReadOnlyList<InstalledExecutionRuntimeSummary>? _executionRuntimes;

    public IReadOnlyList<SessionPackageDescriptor> SessionPackages => _sessionPackages;

    public IReadOnlyList<InstalledPackageDescriptor> InstalledPackages => _installedPackages;

    public IReadOnlyList<RegistryPackageUpdate> AvailableUpdates => _availableUpdates;

    public bool IsEmpty => _installedPackages.Count == 0 && _sessionPackages.Count == 0;

    public int InstalledPackageCount => _installedPackages.Count;

    public int ActivePackageCount => _sessionPackages.Count(package => package.IsEnabled);

    public int DisabledPackageCount => _installedPackages.Count(package => !package.IsEnabled);

    public int FailedPackageCount => _sessionPackages.Count(package => package.Readiness == PackageReadinessState.Failed);

    public int AvailableUpdateCount => _availableUpdates.Count;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var sessionPackagesTask = runtimeApiClient.GetSessionPackagesAsync(cancellationToken);
        var installedPackagesTask = runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        var executionRuntimesTask = runtimeApiClient.GetExecutionRuntimesAsync(cancellationToken);
        await Task.WhenAll(sessionPackagesTask, installedPackagesTask, executionRuntimesTask);

        ReplaceItems(_sessionPackages, await sessionPackagesTask.ConfigureAwait(false));
        ReplaceItems(_installedPackages, await installedPackagesTask.ConfigureAwait(false));
        _executionRuntimes = await executionRuntimesTask.ConfigureAwait(false);
        ApplyUpdateResults();
    }

    public async Task RefreshExecutionRuntimesAsync(CancellationToken cancellationToken = default)
        => _executionRuntimes = await runtimeApiClient.GetExecutionRuntimesAsync(cancellationToken);

    public void ApplySessionPackages(IReadOnlyList<SessionPackageDescriptor> sessionPackages)
        => ReplaceItems(_sessionPackages, sessionPackages);

    public async Task RefreshInstalledRecordsAsync(CancellationToken cancellationToken)
    {
        var installed = await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        ReplaceItems(_installedPackages, installed);
        ApplyUpdateResults();
    }

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.FirstOrDefault(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _availableUpdates.FirstOrDefault(update => string.Equals(update.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PackageExecutionRuntimeStatusViewModel> GetExecutionRuntimeStatuses(string packageId)
        => GetInstalledPackage(packageId)?.ExecutionRuntimeBindings
            .Select(binding => _executionRuntimes is null
                ? PackageExecutionRuntimeStatusViewModel.Unchecked(binding)
                : PackageExecutionRuntimeStatusViewModel.Create(binding,
                    _executionRuntimes.FirstOrDefault(runtime => runtime.Identity == binding.Runtime)))
            .OrderBy(status => status.Role, StringComparer.Ordinal)
            .ThenBy(status => status.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(status => status.Identity.Rid, StringComparer.Ordinal)
            .ToArray()
            ?? [];

    public void ApplyUpdateResults() => ReplaceItems(_availableUpdates, updateChecks?.ForInstalled(_installedPackages) ?? []);

    private static void ReplaceItems<T>(List<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        target.AddRange(items);
    }
}
