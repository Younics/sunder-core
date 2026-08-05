using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class InstalledPackageLifecycleService
{
    private const int MaxPendingStages = 32;
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _gate;
    private readonly InstalledPackageStore _installedPackages;
    private readonly PackageStoreCoordinator _storeCoordinator;
    private readonly PackageSessionReconciler _reconciler;
    private readonly RuntimeContentTransferStore _transfers;
    private readonly PackageSessionPublisher _publisher;
    private readonly ILogger<InstalledPackageLifecycleService> _logger;
    private readonly IInstalledPackageLifecycleFaultInjector? _faultInjector;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeRpcCatalog? _rpcCatalog;
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reconciliationPendingStages = new(StringComparer.OrdinalIgnoreCase);

    public InstalledPackageLifecycleService(
        RuntimeSessionOwner sessions,
        RuntimeOperationGate gate,
        InstalledPackageStore installedPackages,
        PackageStoreCoordinator storeCoordinator,
        PackageSessionReconciler reconciler,
        RuntimeContentTransferStore transfers,
        PackageSessionPublisher publisher,
        ILogger<InstalledPackageLifecycleService> logger,
        IInstalledPackageLifecycleFaultInjector? faultInjector = null,
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
        TimeProvider? timeProvider = null,
        RuntimeRpcCatalog? rpcCatalog = null)
    {
        _sessions = sessions;
        _gate = gate;
        _installedPackages = installedPackages;
        _storeCoordinator = storeCoordinator;
        _reconciler = reconciler;
        _transfers = transfers;
        _publisher = publisher;
        _logger = logger;
        _faultInjector = faultInjector;
        _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rpcCatalog = rpcCatalog;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await _storeCoordinator.InitializeAsync(operation.CancellationToken);
    }

    internal RuntimePackageStamp Stamp => _sessions.Stamp;

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledAsync(CancellationToken cancellationToken = default)
        => (await _installedPackages.ListAsync(cancellationToken))
            .Select(_installedPackages.ToDescriptor)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async Task<PackageOperationResult> InstallFromRuntimePathAsync(string path, CancellationToken cancellationToken = default)
        => await ExecutePreparedMutationAsync(
            new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: path),
            cancellationToken);

    internal async Task<PackageOperationResult> UpgradeFromRuntimePathAsync(
        string packageId,
        string path,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default)
        => await ExecutePreparedMutationAsync(
            new PackageStoreMutation(PackageStoreMutationKind.Upgrade, packageId, path, allowDowngrade, reinstall),
            cancellationToken);

    public async Task<PackageLifecycleOperationResult> LoadInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var baseGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var sources = _sessions.Sources.Snapshot();
        sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload, PackageSessionOverlayOwner.Sdk);
        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, operationToken);
        var warnings = loaded.Warnings.Concat(loaded.Errors.Select(error => $"Installed package session loaded with package errors: {error}")).ToList();
        if (loaded.Session is null)
        {
            return PackageLifecycleOperationResult.Failed(
                loaded.Errors.FirstOrDefault() ?? "Installed package lifecycle load failed.",
                currentPackages,
                warnings,
                loaded.Errors);
        }

        if (loaded.Session.IsEmpty && _sessions.State.ActiveSession.IsEmpty)
        {
            _rpcCatalog?.DeactivateAll(_sessions.Generation);
            _rpcCatalog?.VerifySessionGeneration(_sessions.Generation);
            ResolvePendingReconciliations(_sessions.Stamp);
            return new PackageLifecycleOperationResult(
                true,
                "No installed packages to load.",
                currentPackages,
                warnings,
                [],
                [])
            {
                CommittedStamp = _sessions.Stamp,
            };
        }

        var packages = loaded.Session.GetActivePackages();
        try
        {
            var publication = await _publisher.PublishAsync(
                loaded.Session,
                sources,
                warnings,
                loaded.Errors,
                baseGeneration,
                operationToken);
            warnings.AddRange(publication.CleanupWarnings);
            var impacted = currentPackages.Select(package => package.PackageId)
                .Concat(packages.Select(package => package.PackageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var committed = _sessions.GetSnapshot();
            ResolvePendingReconciliations(publication.Stamp);
            return new PackageLifecycleOperationResult(
                true,
                "Installed packages loaded.",
                committed.ActivePackages,
                warnings,
                loaded.Errors,
                impacted)
            {
                CommittedStamp = publication.Stamp,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to start background services for installed package lifecycle load");
            const string message = "Installed package lifecycle load failed while starting background services.";
            return PackageLifecycleOperationResult.Failed(message, currentPackages, warnings, [message]);
        }
        throw new InvalidOperationException("Installed package publication did not complete.");
    }

    public Task<PackageOperationResult> SetEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken = default)
        => ExecutePreparedMutationAsync(
            new PackageStoreMutation(enabled ? PackageStoreMutationKind.Enable : PackageStoreMutationKind.Disable, packageId),
            cancellationToken);

    public Task<PackageOperationResult> UninstallAsync(
        string packageId,
        PackageUninstallRequest request,
        CancellationToken cancellationToken = default)
        => ExecutePreparedMutationAsync(
            new PackageStoreMutation(
                PackageStoreMutationKind.Uninstall,
                packageId,
                AllowCascade: request.AllowCascade,
                ConfirmationToken: request.ConfirmationToken),
            cancellationToken);

    public async Task<PackageUninstallPlan?> GetUninstallPlanAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var packages = await _installedPackages.ListAsync(cancellationToken);
        return packages.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ? PackageStorePolicy.CreateUninstallPlan(packageId, packages)
            : null;
    }

    public Task<PackageStoreStageResult> StageAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
        => StageCoreAsync(
            request,
            registryProvenanceByUploadId: null,
            registryStateExpectations: null,
            cancellationToken);

    internal Task<PackageStoreStageResult> StageRegistryAsync(
        PackageStoreStageRequest request,
        IReadOnlyDictionary<string, InstalledPackageProvenanceRecord> registryProvenanceByUploadId,
        IReadOnlyList<RegistryPackageStateExpectation> registryStateExpectations,
        CancellationToken cancellationToken = default)
        => StageCoreAsync(
            request,
            registryProvenanceByUploadId,
            registryStateExpectations,
            cancellationToken);

    private async Task<PackageStoreStageResult> StageCoreAsync(
        PackageStoreStageRequest request,
        IReadOnlyDictionary<string, InstalledPackageProvenanceRecord>? registryProvenanceByUploadId,
        IReadOnlyList<RegistryPackageStateExpectation>? registryStateExpectations,
        CancellationToken cancellationToken)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (_stages.Count >= MaxPendingStages)
        {
            return PackageStoreStageResult.Failed(
                $"The Runtime already has {MaxPendingStages} pending package store stages.",
                _sessions.State.GetActivePackages());
        }
        var baseSessionGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var sources = _sessions.Sources.Snapshot();
        var leases = new List<RuntimeUploadLease>();
        PackageStoreStagePreparation preparation;
        try
        {
            if (registryStateExpectations is not null)
            {
                await ValidateRegistryStateExpectationsAsync(
                    registryStateExpectations,
                    operationToken);
            }
            preparation = await _storeCoordinator.PrepareStageAsync(
                ResolveMutations(request.Mutations, leases, registryProvenanceByUploadId, operationToken),
                operationToken);
        }
        catch (InvalidDataException exception)
        {
            return PackageStoreStageResult.Failed(exception.Message, currentPackages);
        }
        finally
        {
            foreach (var lease in leases) _transfers.ReleaseUpload(lease);
        }
        return await PrepareCandidateAsync(
            preparation,
            baseSessionGeneration,
            currentPackages,
            sources,
            operationToken);
    }

    private async Task ValidateRegistryStateExpectationsAsync(
        IReadOnlyList<RegistryPackageStateExpectation> expectations,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RegistryPackageStateExpectation> expectedByPackageId;
        try
        {
            expectedByPackageId = expectations.ToDictionary(
                expectation => expectation.PackageId,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Registry package staging contains duplicate installed-state expectations.",
                exception);
        }

        var current = (await _installedPackages.ListAsync(cancellationToken))
            .ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var expectation in expectedByPackageId.Values)
        {
            if (expectation.Version is null)
            {
                if (current.ContainsKey(expectation.PackageId))
                {
                    throw StaleRegistryPlan(expectation.PackageId);
                }
                continue;
            }

            if (!current.TryGetValue(expectation.PackageId, out var package)
                || !string.Equals(package.Version, expectation.Version, StringComparison.Ordinal)
                || !Equals(
                    package.Provenance ?? InstalledPackageProvenanceRecord.Unknown,
                    expectation.Provenance ?? InstalledPackageProvenanceRecord.Unknown))
            {
                throw StaleRegistryPlan(expectation.PackageId);
            }
        }

        var expectedInstalledCount = expectedByPackageId.Values.Count(expectation => expectation.Version is not null);
        if (current.Count != expectedInstalledCount)
        {
            var unexpected = current.Keys.FirstOrDefault(packageId =>
                !expectedByPackageId.TryGetValue(packageId, out var expectation)
                || expectation.Version is null);
            throw StaleRegistryPlan(unexpected);
        }
    }

    private static InvalidDataException StaleRegistryPlan(string? packageId)
        => new(packageId is null
            ? "Registry package staging is stale because the installed package catalog changed after resolution."
            : $"Registry package staging is stale because installed state for package '{packageId}' changed after resolution.");

    private async Task<PackageStoreStageResult> PrepareCandidateAsync(
        PackageStoreStagePreparation preparation,
        long baseSessionGeneration,
        IReadOnlyList<ActivePackageDescriptor> currentPackages,
        PackageSessionSourceSnapshot sources,
        CancellationToken cancellationToken)
    {
        if (!preparation.Success || preparation.Stage is null)
        {
            var failure = preparation.Failure ?? PackageOperationResults.Failure("Package store stage failed.");
            return PackageStoreStageResult.Failed(failure.Message ?? "Package store stage failed.", currentPackages, failure.Warnings, failure.Errors, failure.ImpactedPackageIds);
        }

        if (preparation.Stage.Result.ImpactedPackageIds.Count == 0)
        {
            var createdAtUtc = _timeProvider.GetUtcNow();
            var expiresAtUtc = createdAtUtc + _lifecyclePolicy.PendingStageLifetime;
            _stages[preparation.Stage.StageId] = new PendingStoreStage(
                Candidate: null,
                preparation.Stage.BaseCatalogGeneration,
                baseSessionGeneration,
                createdAtUtc,
                expiresAtUtc);
            _sessions.RegisterStage(
                preparation.Stage.StageId,
                baseSessionGeneration,
                baseSessionGeneration,
                RuntimePackageStageKind.PackageStore,
                createdAtUtc,
                expiresAtUtc);
            return new PackageStoreStageResult(
                preparation.Stage.StageId,
                preparation.Stage.Result,
                currentPackages);
        }

        PackageSessionLoadResult loaded;
        try
        {
            loaded = await _reconciler.LoadMergedSessionAsync(
                preparation.Stage.ProspectivePackages,
                sources.ActiveDevOverlays,
                preparation.Stage.PreparationSourcePaths,
                cancellationToken);
        }
        catch
        {
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            throw;
        }
        if (loaded.Session is null || loaded.Errors.Count > 0)
        {
            if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            return PackageStoreStageResult.Failed(
                loaded.Errors.FirstOrDefault() ?? "Package store stage failed while loading the prospective package session.",
                currentPackages,
                preparation.Stage.Result.Warnings.Concat(loaded.Warnings).ToArray(),
                loaded.Errors,
                preparation.Stage.Result.ImpactedPackageIds);
        }
        if (_sessions.Generation != baseSessionGeneration)
        {
            await loaded.Session.DisposeAsync();
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            return PackageStoreStageResult.Failed(
                "Package store stage is stale because the active package session changed while it was being prepared.",
                currentPackages,
                impactedPackageIds: preparation.Stage.Result.ImpactedPackageIds);
        }

        var result = preparation.Stage.Result with
        {
            Warnings = preparation.Stage.Result.Warnings
                .Concat(loaded.Warnings)
                .Concat(loaded.Errors.Select(error => $"Staged installed package session loaded with package errors: {error}"))
                .ToArray(),
        };
        PreparedPackageSession candidate;
        try
        {
            candidate = _publisher.Prepare(
                loaded.Session,
                sources,
                result.Warnings,
                loaded.Errors,
                baseSessionGeneration,
                preparation.Stage.StageId);
        }
        catch (Exception exception)
        {
            await loaded.Session.DisposeAsync();
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            _logger.LogError(exception, "Failed to prepare package store stage {StageId}", preparation.Stage.StageId);
            return PackageStoreStageResult.Failed(
                "Package store stage candidate preparation failed.",
                currentPackages,
                result.Warnings,
                impactedPackageIds: result.ImpactedPackageIds);
        }
        var candidateCreatedAtUtc = _timeProvider.GetUtcNow();
        var candidateExpiresAtUtc = candidateCreatedAtUtc + _lifecyclePolicy.PendingStageLifetime;
        _stages[preparation.Stage.StageId] = new PendingStoreStage(
            candidate,
            preparation.Stage.BaseCatalogGeneration,
            baseSessionGeneration,
            candidateCreatedAtUtc,
            candidateExpiresAtUtc);
        _sessions.RegisterStage(
            preparation.Stage.StageId,
            baseSessionGeneration + 1,
            baseSessionGeneration,
            RuntimePackageStageKind.PackageStore,
            candidateCreatedAtUtc,
            candidateExpiresAtUtc);
        return new PackageStoreStageResult(
            preparation.Stage.StageId,
            result,
            loaded.Session.GetActivePackages());
    }

    public async Task<PackageOperationResult> CommitStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        return await CommitStageCoreAsync(stageId, operation.CancellationToken);
    }

    private async Task<PackageOperationResult> CommitStageCoreAsync(string stageId, CancellationToken operationToken)
    {
        if (!_stages.Remove(stageId, out var stage))
        {
            return PackageOperationResults.Failure(
                _sessions.GetStageStatus(stageId)?.Message
                ?? $"Package store stage '{stageId}' was not found.");
        }
        if ((stage.Candidate is not null && stage.BaseSessionGeneration != _sessions.Generation)
            || stage.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration)
        {
            if (stage.Candidate is not null)
            {
                await _publisher.DiscardAsync(stage.Candidate);
            }
            else
            {
                _sessions.RemoveStage(stageId);
            }
            await _storeCoordinator.DiscardStageAsync(stageId);
            _sessions.MarkStageFailed(stageId, "The package store stage became stale before commit.");
            return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
        }

        PendingPackageSessionPublication? publication = null;
        PackageOperationResult? storeResult = null;
        var storeCommitted = false;
        _sessions.MarkStageCommitting(stageId);
        try
        {
            if (stage.Candidate is not null)
            {
                publication = await _publisher.BeginPublishAsync(stage.Candidate, operationToken);
                if (stage.BaseSessionGeneration != _sessions.Generation
                    || stage.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration)
                {
                    await _publisher.DiscardAsync(publication);
                    publication = null;
                    await _storeCoordinator.DiscardStageAsync(stageId);
                    _sessions.MarkStageFailed(stageId, "The package store stage became stale before commit.");
                    return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
                }
            }

            operationToken.ThrowIfCancellationRequested();
            storeResult = await _storeCoordinator.CommitStageAsync(stageId, CancellationToken.None);
            if (!storeResult.Success)
            {
                if (publication is not null)
                {
                    await _publisher.DiscardAsync(publication);
                    publication = null;
                }
                else
                {
                    _sessions.RemoveStage(stageId);
                }
                _sessions.MarkStageFailed(stageId, storeResult.Message);
                return storeResult;
            }
            storeCommitted = true;

            if (publication is null)
            {
                _sessions.RemoveStage(stageId);
                var stamp = _sessions.Stamp;
                var noOpResult = storeResult with
                {
                    StoreCommitted = true,
                    CommittedStamp = stamp,
                };
                _sessions.MarkStageCommitted(
                    stageId,
                    stamp,
                    runtimeSessionApplied: false,
                    reconciliationPending: false,
                    noOpResult.Message);
                return noOpResult;
            }

            stage.Candidate!.Session.FinalizeCommittedInstalledSources();
            _faultInjector?.Hit(InstalledPackageLifecycleFaultPoint.StoreCommittedBeforeSessionPublication);
            var committed = await _publisher.CommitAsync(publication, operationToken);
            publication = null;
            var result = storeResult with
            {
                RuntimeSessionApplied = true,
                RequiresAppRestart = false,
                Warnings = storeResult.Warnings.Concat(committed.CleanupWarnings).ToArray(),
                CommittedStamp = committed.Stamp,
                StoreCommitted = true,
                RuntimeSessionReconciliationPending = committed.ReconciliationPending,
            };
            _sessions.MarkStageCommitted(
                stageId,
                committed.Stamp,
                runtimeSessionApplied: true,
                committed.ReconciliationPending,
                result.Message);
            if (committed.ReconciliationPending)
            {
                _reconciliationPendingStages.Add(stageId);
            }
            else
            {
                ResolvePendingReconciliations(committed.Stamp);
            }
            return result;
        }
        catch (OperationCanceledException) when (!storeCommitted)
        {
            if (publication is not null && !publication.Publication.Completed)
            {
                await _publisher.DiscardAsync(publication);
            }
            else
            {
                _sessions.RemoveStage(stageId);
            }
            await _storeCoordinator.DiscardStageAsync(stageId);
            _sessions.MarkStageFailed(stageId, "The package store stage was cancelled before commit.");
            throw;
        }
        catch (Exception exception)
        {
            if (publication is not null && !publication.Publication.Completed)
            {
                if (storeCommitted)
                {
                    try
                    {
                        await _publisher.DiscardAsync(publication);
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(
                            cleanupException,
                            "Failed to discard package session candidate after committed store stage {StageId}",
                            stageId);
                    }
                }
                else
                {
                    await _publisher.DiscardAsync(publication);
                }
            }
            else
            {
                _sessions.RemoveStage(stageId);
            }

            if (!storeCommitted)
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
                _sessions.MarkStageFailed(stageId, "The package store stage could not be committed.");
                _logger.LogError(exception, "Failed to commit package store stage {StageId}", stageId);
                return PackageOperationResults.Failure("The package store stage could not be committed.");
            }

            const string warning = "The installed package store was committed, but the Runtime session was not updated; reconciliation is pending.";
            _logger.LogError(exception, "Package store stage {StageId} failed after its durable store commit", stageId);
            var stamp = _sessions.Stamp;
            var degraded = storeResult! with
            {
                Success = true,
                RuntimeSessionApplied = false,
                RequiresAppRestart = false,
                Warnings = storeResult.Warnings.Append(warning).ToArray(),
                Errors = [],
                CommittedStamp = stamp,
                StoreCommitted = true,
                RuntimeSessionReconciliationPending = true,
            };
            _sessions.MarkStageCommitted(
                stageId,
                stamp,
                runtimeSessionApplied: false,
                reconciliationPending: true,
                degraded.Message);
            _reconciliationPendingStages.Add(stageId);
            return degraded;
        }
    }

    public async Task<bool> DiscardStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        operation.CancellationToken.ThrowIfCancellationRequested();
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (!_stages.Remove(stageId, out var stage)) return false;
        if (stage.Candidate is not null)
        {
            await _publisher.DiscardAsync(stage.Candidate);
        }
        else
        {
            _sessions.RemoveStage(stageId);
        }
        operation.CancellationToken.ThrowIfCancellationRequested();
        var discarded = await _storeCoordinator.DiscardStageAsync(stageId);
        if (discarded)
        {
            _sessions.MarkStageDiscarded(stageId);
        }
        return discarded;
    }

    private async Task<PackageOperationResult> ExecutePreparedMutationAsync(
        PackageStoreMutation mutation,
        CancellationToken cancellationToken)
    {
        await using (var operation = await _gate.EnterAsync(cancellationToken))
        {
            var operationToken = operation.CancellationToken;
            var baseSessionGeneration = _sessions.Generation;
            var currentPackages = _sessions.State.GetActivePackages();
            var sources = _sessions.Sources.Snapshot();
            var preparation = await _storeCoordinator.PrepareStageAsync([mutation], operationToken);
            var stage = await PrepareCandidateAsync(
                preparation,
                baseSessionGeneration,
                currentPackages,
                sources,
                operationToken);
            return !stage.Success || stage.StageId is null
                ? stage.OperationResult
                : await CommitStageCoreAsync(stage.StageId, operationToken);
        }
    }

    private IReadOnlyList<PackageStoreMutation> ResolveMutations(
        IReadOnlyList<PackageStoreMutationRequest> requests,
        ICollection<RuntimeUploadLease> leases,
        IReadOnlyDictionary<string, InstalledPackageProvenanceRecord>? registryProvenanceByUploadId,
        CancellationToken cancellationToken)
    {
        var mutations = new List<PackageStoreMutation>(requests.Count);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? archivePath = null;
            if (request.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade)
            {
                if (string.IsNullOrWhiteSpace(request.UploadId)) throw new InvalidDataException("An upload id is required for package install and upgrade mutations.");
                var lease = _transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Package, _sessions.Generation, consume: true)
                    ?? throw new InvalidDataException("The package upload was not found or belongs to a stale Runtime generation.");
                leases.Add(lease);
                archivePath = lease.FilePath;
                var provenance = registryProvenanceByUploadId is null
                    ? InstalledPackageProvenanceRecord.LocalArchive(lease.ContentHash)
                    : registryProvenanceByUploadId.TryGetValue(request.UploadId, out var registryProvenance)
                        ? registryProvenance
                        : throw new InvalidDataException(
                            $"Registry provenance is missing for package upload '{request.UploadId}'.");
                mutations.Add(new PackageStoreMutation(
                    request.Kind,
                    request.PackageId,
                    archivePath,
                    request.AllowDowngrade,
                    request.Reinstall,
                    provenance,
                    request.AllowCascade,
                    request.ConfirmationToken));
                continue;
            }
            mutations.Add(new PackageStoreMutation(
                request.Kind,
                request.PackageId,
                archivePath,
                request.AllowDowngrade,
                request.Reinstall,
                AllowCascade: request.AllowCascade,
                ConfirmationToken: request.ConfirmationToken));
        }
        return mutations;
    }

    internal ActivePackageSession? GetStagedSession(string stageId)
        => _stages.TryGetValue(stageId, out var stage) ? stage.Candidate?.Session : null;

    private void ResolvePendingReconciliations(RuntimePackageStamp stamp)
    {
        foreach (var stageId in _reconciliationPendingStages)
        {
            _sessions.MarkStageCommitted(
                stageId,
                stamp,
                runtimeSessionApplied: true,
                reconciliationPending: false,
                "The committed package store stage was reconciled to the Runtime session.");
        }
        _reconciliationPendingStages.Clear();
    }

    private sealed record PendingStoreStage(
        PreparedPackageSession? Candidate,
        long BaseCatalogGeneration,
        long BaseSessionGeneration,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}

internal enum InstalledPackageLifecycleFaultPoint
{
    StoreCommittedBeforeSessionPublication,
}

internal interface IInstalledPackageLifecycleFaultInjector
{
    void Hit(InstalledPackageLifecycleFaultPoint point);
}
