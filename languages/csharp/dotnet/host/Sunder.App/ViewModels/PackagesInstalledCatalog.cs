using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackagesInstalledCatalog(
    IRuntimePackagesClient runtimeApiClient)
{
    private readonly List<SessionPackageDescriptor> _sessionPackages = [];
    private readonly List<InstalledPackageDescriptor> _installedPackages = [];
    private readonly List<RegistryPackageUpdate> _availableUpdates = [];
    private readonly List<InstalledExecutionRuntimeSummary> _executionRuntimes = [];

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
        var executionRuntimesTask = runtimeApiClient.GetExecutionRuntimesAsync(cancellationToken);
        await Task.WhenAll(sessionPackagesTask, installedPackagesTask, executionRuntimesTask).ConfigureAwait(false);

        ReplaceItems(_sessionPackages, await sessionPackagesTask.ConfigureAwait(false));
        ReplaceItems(_installedPackages, await installedPackagesTask.ConfigureAwait(false));
        ReplaceItems(_executionRuntimes, await executionRuntimesTask.ConfigureAwait(false));
        await ResolveAvailableUpdatesAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshInstalledPackageStateOnlyAsync(Action<string> addWarning, CancellationToken cancellationToken = default)
    {
        var installedPackagesTask = runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        var executionRuntimesTask = runtimeApiClient.GetExecutionRuntimesAsync(cancellationToken);
        await Task.WhenAll(installedPackagesTask, executionRuntimesTask).ConfigureAwait(false);
        ReplaceItems(_installedPackages, await installedPackagesTask.ConfigureAwait(false));
        ReplaceItems(_executionRuntimes, await executionRuntimesTask.ConfigureAwait(false));
        await ResolveAvailableUpdatesAsync(addWarning, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshExecutionRuntimesAsync(CancellationToken cancellationToken = default)
        => ReplaceItems(
            _executionRuntimes,
            await runtimeApiClient.GetExecutionRuntimesAsync(cancellationToken).ConfigureAwait(false));

    public void ApplySessionPackages(IReadOnlyList<SessionPackageDescriptor> sessionPackages)
        => ReplaceItems(_sessionPackages, sessionPackages);

    public InstalledPackageDescriptor? GetInstalledPackage(string packageId)
        => _installedPackages.FirstOrDefault(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public RegistryPackageUpdate? GetPackageUpdate(string packageId)
        => _availableUpdates.FirstOrDefault(update => string.Equals(update.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PackageExecutionRuntimeStatusViewModel> GetExecutionRuntimeStatuses(string packageId)
        => GetInstalledPackage(packageId)?.ExecutionRuntimeBindings
            .Select(binding => PackageExecutionRuntimeStatusViewModel.Create(
                binding,
                _executionRuntimes.FirstOrDefault(runtime => IdentityMatches(runtime.Identity, binding.Runtime))))
            .OrderBy(status => status.Role, StringComparer.Ordinal)
            .ThenBy(status => status.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(status => status.Identity.Rid, StringComparer.Ordinal)
            .ToArray()
            ?? [];

    private async Task ResolveAvailableUpdatesAsync(Action<string> addWarning, CancellationToken cancellationToken)
    {
        _availableUpdates.Clear();
        if (_installedPackages.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var request in InstalledPackageUpdateRequestBuilder.Build(_installedPackages))
            {
                var requestedPackageIds = request.Packages
                    .Select(package => package.PackageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (!plan.Success)
                {
                    addWarning($"Registry update check failed for {request.RegistryOrigin}: {plan.Errors.FirstOrDefault() ?? "plan resolution failed"}");
                    continue;
                }
                _availableUpdates.AddRange(plan.Items
                    .Where(item => requestedPackageIds.Contains(item.PackageId)
                                   && item.ActionKind == RegistryPackageActionKinds.PackageUpdate
                                   && item.CurrentVersion is not null
                                   && item.Version is not null
                                   && !string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
                    .Select(item => new RegistryPackageUpdate(
                        item.PackageId,
                        item.CurrentVersion!,
                        item.Version!,
                        item.DeprecatedMessage,
                        item.Artifacts.Select(artifact => new RegistryPackageProjectionArtifact(
                            artifact.Kind,
                            artifact.Rid,
                            artifact.Sha256,
                            artifact.Size,
                            artifact.DownloadUrl,
                            artifact.SourceArchiveSha256,
                            artifact.ManifestSha256,
                            artifact.ProjectionContentIdentity,
                            artifact.ProjectionFormatVersion)).ToArray())));
            }
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

    private static bool IdentityMatches(
        RuntimeExecutionRuntimeIdentity installed,
        RuntimeExecutionRuntimeIdentity binding)
        => installed == binding;
}
