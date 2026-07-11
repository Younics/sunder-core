using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class InstalledPackageLifecycleService
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _gate;
    private readonly InstalledPackageStore _installedPackages;
    private readonly PackageStoreCoordinator _storeCoordinator;
    private readonly PackageSessionReconciler _reconciler;
    private readonly RuntimePackageUiService _ui;
    private readonly RuntimeContentTransferStore _transfers;
    private readonly ILogger<InstalledPackageLifecycleService> _logger;
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.OrdinalIgnoreCase);

    public InstalledPackageLifecycleService(
        RuntimeSessionOwner sessions,
        RuntimeOperationGate gate,
        InstalledPackageStore installedPackages,
        PackageStoreCoordinator storeCoordinator,
        PackageSessionReconciler reconciler,
        RuntimePackageUiService ui,
        RuntimeContentTransferStore transfers,
        ILogger<InstalledPackageLifecycleService> logger)
    {
        _sessions = sessions;
        _gate = gate;
        _installedPackages = installedPackages;
        _storeCoordinator = storeCoordinator;
        _reconciler = reconciler;
        _ui = ui;
        _transfers = transfers;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _storeCoordinator.InitializeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledAsync(CancellationToken cancellationToken = default)
        => (await _installedPackages.ListAsync(cancellationToken))
            .Select(_installedPackages.ToDescriptor)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async Task<PackageOperationResult> InstallFromRuntimePathAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _storeCoordinator.ExecuteAsync(
                [new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: path)],
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageLifecycleOperationResult> LoadInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentPackages = _sessions.State.GetActivePackages();
            var currentSnapshots = _ui.GetActiveSnapshots();
            var sources = _sessions.Sources.Snapshot();
            sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload, PackageSessionOverlayOwner.Sdk);
            var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            var warnings = loaded.Warnings.Concat(loaded.Errors.Select(error => $"Installed package session loaded with package errors: {error}")).ToList();
            if (loaded.Session is null)
            {
                return PackageLifecycleOperationResult.Failed(
                    loaded.Errors.FirstOrDefault() ?? "Installed package lifecycle load failed.",
                    currentPackages,
                    currentSnapshots,
                    warnings,
                    loaded.Errors);
            }

            var packages = loaded.Session.GetActivePackages();
            var packageSources = loaded.Session.GetActivePackageSources();
            try
            {
                await loaded.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await loaded.Session.DisposeAsync();
                _logger.LogError(exception, "Failed to start background services for installed package lifecycle load");
                const string message = "Installed package lifecycle load failed while starting background services.";
                return PackageLifecycleOperationResult.Failed(message, currentPackages, currentSnapshots, warnings, [message]);
            }

            warnings.AddRange(await _sessions.State.ClearActiveSessionAsync());
            _sessions.Sources.Replace(sources);
            _sessions.Publish(loaded.Session);
            var impacted = currentPackages.Select(package => package.PackageId)
                .Concat(packages.Select(package => package.PackageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PackageLifecycleOperationResult(true, "Installed packages loaded.", packages, _ui.CreateSnapshots(packageSources, _sessions.Generation), warnings, [], impacted);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PackageOperationResult> ReloadAsync(InstalledPackageSessionReloadRequest request, CancellationToken cancellationToken = default)
    {
        var impacted = request.ImpactedPackageIds
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ReloadAfterMutationAsync(PackageOperationResults.Success("Installed package session reloaded.", impactedPackageIds: impacted), cancellationToken);
    }

    public Task<PackageOperationResult> SetEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken = default)
        => ExecuteAndReloadAsync(
            new PackageStoreMutation(enabled ? PackageStoreMutationKind.Enable : PackageStoreMutationKind.Disable, packageId),
            cancellationToken);

    public Task<PackageOperationResult> UninstallAsync(string packageId, CancellationToken cancellationToken = default)
        => ExecuteAndReloadAsync(new PackageStoreMutation(PackageStoreMutationKind.Uninstall, packageId), cancellationToken);

    public async Task<PackageStoreStageResult> StageAsync(PackageStoreStageRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentPackages = _sessions.State.GetActivePackages();
            var currentSnapshots = _ui.GetActiveSnapshots();
            var leases = new List<RuntimeUploadLease>();
            PackageStoreStagePreparation preparation;
            try
            {
                preparation = await _storeCoordinator.PrepareStageAsync(ResolveMutations(request.Mutations, leases), cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                return PackageStoreStageResult.Failed(exception.Message, currentPackages, currentSnapshots);
            }
            finally
            {
                foreach (var lease in leases) _transfers.ReleaseUpload(lease);
            }
            if (!preparation.Success || preparation.Stage is null)
            {
                var failure = preparation.Failure ?? PackageOperationResults.Failure("Package store stage failed.");
                return PackageStoreStageResult.Failed(failure.Message ?? "Package store stage failed.", currentPackages, currentSnapshots, failure.Warnings, failure.Errors, failure.ImpactedPackageIds);
            }

            var loaded = await _reconciler.LoadMergedSessionAsync(
                preparation.Stage.ProspectivePackages,
                _sessions.Sources.Snapshot().ActiveDevOverlays,
                startBackgroundServices: false,
                cancellationToken);
            if (loaded.Session is null)
            {
                await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
                return PackageStoreStageResult.Failed(
                    loaded.Errors.FirstOrDefault() ?? "Package store stage failed while loading the prospective package session.",
                    currentPackages,
                    currentSnapshots,
                    preparation.Stage.Result.Warnings.Concat(loaded.Warnings).ToArray(),
                    loaded.Errors,
                    preparation.Stage.Result.ImpactedPackageIds);
            }

            var result = preparation.Stage.Result with
            {
                Warnings = preparation.Stage.Result.Warnings
                    .Concat(loaded.Warnings)
                    .Concat(loaded.Errors.Select(error => $"Staged installed package session loaded with package errors: {error}"))
                    .ToArray(),
            };
            _stages[preparation.Stage.StageId] = new PendingStoreStage(loaded.Session, _sessions.Generation, preparation.Stage.BaseCatalogGeneration);
            _sessions.RegisterStage(preparation.Stage.StageId, _sessions.Generation + 1);
            return new PackageStoreStageResult(
                preparation.Stage.StageId,
                result,
                loaded.Session.GetActivePackages(),
                _ui.CreateSnapshots(loaded.Session.GetActivePackageSources(), _sessions.Generation + 1, preparation.Stage.StageId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageOperationResult> CommitStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stages.Remove(stageId, out var stage)) return PackageOperationResults.Failure($"Package store stage '{stageId}' was not found.");
            _ui.DiscardStage(stageId);
            await stage.Session.DisposeAsync();
            if (stage.BaseSessionGeneration != _sessions.Generation || stage.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration)
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
                return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
            }
            try
            {
                var result = await _storeCoordinator.CommitStageAsync(stageId, cancellationToken);
                return await ReloadAfterMutationCoreAsync(result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to commit package store stage {StageId}", stageId);
                return PackageOperationResults.Failure("The package store stage could not be committed.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DiscardStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stages.Remove(stageId, out var stage)) return false;
            await stage.Session.DisposeAsync();
            _ui.DiscardStage(stageId);
            return await _storeCoordinator.DiscardStageAsync(stageId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownAsync(PackageSessionLifecycleService sessionLifecycle)
    {
        await _gate.ShutdownAsync(async () =>
        {
            await sessionLifecycle.ShutdownStagesAsync();
            foreach (var stage in _stages.Values) await stage.Session.DisposeAsync();
            _stages.Clear();
            _sessions.ClearStages();
            await _storeCoordinator.DiscardAllStagesAsync();
            _sessions.Auth.Clear();
            await _sessions.State.ClearActiveSessionAsync();
            RuntimePackageSessionDirectories.CleanupStaleSessions();
        });
    }

    private async Task<PackageOperationResult> ExecuteAndReloadAsync(PackageStoreMutation mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReloadAfterMutationCoreAsync(await _storeCoordinator.ExecuteAsync([mutation], cancellationToken), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PackageOperationResult> ReloadAfterMutationAsync(PackageOperationResult result, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReloadAfterMutationCoreAsync(result, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PackageOperationResult> ReloadAfterMutationCoreAsync(PackageOperationResult result, CancellationToken cancellationToken)
    {
        if (!result.Success || result.ImpactedPackageIds.Count == 0)
        {
            return result.Success ? result with { RuntimeSessionApplied = true, RequiresAppRestart = false } : result;
        }
        var warnings = result.Warnings.ToList();
        var loaded = await _reconciler.LoadMergedSessionAsync(_sessions.Sources.Snapshot().ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
        warnings.AddRange(loaded.Warnings);
        if (loaded.Session is null)
        {
            warnings.Add("Installed package changes were saved, but the running package session kept the previous loaded packages.");
            return result with { RuntimeSessionApplied = false, RequiresAppRestart = false, Warnings = warnings };
        }
        warnings.AddRange(loaded.Errors.Select(error => $"Installed package session loaded with package errors: {error}"));
        try
        {
            await loaded.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await loaded.Session.DisposeAsync();
            _logger.LogError(exception, "Failed to apply installed package changes to the running session");
            warnings.Add("Installed package changes were saved, but the running package session kept the previous loaded packages.");
            return result with { RuntimeSessionApplied = false, RequiresAppRestart = false, Warnings = warnings };
        }
        warnings.AddRange(await _sessions.State.ClearActiveSessionAsync());
        _sessions.Publish(loaded.Session);
        return result with { RuntimeSessionApplied = true, RequiresAppRestart = false, Warnings = warnings };
    }

    private IReadOnlyList<PackageStoreMutation> ResolveMutations(IReadOnlyList<PackageStoreMutationRequest> requests, ICollection<RuntimeUploadLease> leases)
    {
        var mutations = new List<PackageStoreMutation>(requests.Count);
        foreach (var request in requests)
        {
            string? archivePath = null;
            if (request.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade)
            {
                if (string.IsNullOrWhiteSpace(request.UploadId)) throw new InvalidDataException("An upload id is required for package install and upgrade mutations.");
                var lease = _transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Package, _sessions.Generation, consume: true)
                    ?? throw new InvalidDataException("The package upload was not found or belongs to a stale Runtime generation.");
                leases.Add(lease);
                archivePath = lease.FilePath;
            }
            mutations.Add(new PackageStoreMutation(request.Kind, request.PackageId, archivePath, request.AllowDowngrade, request.Reinstall));
        }
        return mutations;
    }

    private sealed record PendingStoreStage(ActivePackageSession Session, long BaseSessionGeneration, long BaseCatalogGeneration);
}
