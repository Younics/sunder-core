using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreCoordinator
{
    private readonly RuntimePackagePaths _paths;
    private readonly InstalledPackageStore _store;
    private readonly PackageStoreStageManager _stages;
    private readonly PackageStoreTransactionManager _transactions;
    private readonly PackageStoreRecovery _recovery;
    private readonly PackageLifecyclePolicyCache? _packagePolicies;
    private long _catalogGeneration;
    internal bool HasPreparedStages => _stages.HasStages;

    public PackageStoreCoordinator(
        RuntimePackagePaths paths,
        InstalledPackageStore store,
        SunderPackageArchiveInstaller archiveInstaller,
        IPackageStoreFaultInjector? faultInjector = null,
        PackageLifecyclePolicyCache? packagePolicies = null,
        ILogger<PackageStoreCoordinator>? logger = null)
    {
        _paths = paths;
        _store = store;
        _packagePolicies = packagePolicies;
        _stages = new PackageStoreStageManager(store, archiveInstaller, packagePolicies);
        _transactions = new PackageStoreTransactionManager(paths, store, faultInjector, packagePolicies);
        _recovery = new PackageStoreRecovery(paths, store, _transactions, logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _recovery.InitializeAsync(cancellationToken);
        Interlocked.Increment(ref _catalogGeneration);
    }

    internal Task<TResult> ExecuteUnderPackagePolicyFenceAsync<TResult>(string stageId,
        Func<CancellationToken, Task<TResult>> operation, CancellationToken token)
    {
        if (!_stages.TryGet(stageId, out var stage)) throw new InvalidOperationException("The package policy stage is no longer available.");
        return _packagePolicies is null || stage.PackagePolicyAuthority is not { Count: > 0 } authority
            ? operation(token) : _packagePolicies.ExecuteUnderAuthorityFenceAsync(authority, operation, token);
    }

    public Task<PackageStoreStagePreparation> PrepareStageAsync(
        IReadOnlyList<PackageStoreChange> mutations,
        CancellationToken cancellationToken = default) =>
        PrepareStageAsync(mutations, localBindingAuthority: null, cancellationToken);

    internal async Task<PackageStoreStagePreparation> PrepareStageAsync(
        IReadOnlyList<PackageStoreChange> mutations,
        LocalExecutionRuntimeBindingSnapshot? localBindingAuthority,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pendingRuntimeCutover = (await _recovery.ReadPendingJournalsAsync(cancellationToken)
                    .ConfigureAwait(false))
                .FirstOrDefault(static journal =>
                    journal.RequiresRuntimePublication
                    && journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
                    && journal.CutoverPhase < PackageStoreCutoverPhase.Completed);
            if (pendingRuntimeCutover is not null)
            {
                if (localBindingAuthority is not null)
                {
                    await localBindingAuthority.DisposeAsync().ConfigureAwait(false);
                    localBindingAuthority = null;
                }
                return new PackageStoreStagePreparation(
                    null,
                    PackageOperationResults.Failure(
                        $"Package store transaction '{pendingRuntimeCutover.TransactionId}' is awaiting Runtime session reconciliation."));
            }
            var preparation = await _stages.PrepareAsync(
                    mutations,
                    CatalogGeneration,
                    localBindingAuthority,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!preparation.Success && localBindingAuthority is not null)
            {
                await localBindingAuthority!.DisposeAsync().ConfigureAwait(false);
                localBindingAuthority = null;
            }
            if (preparation.Stage is { } preparedStage)
            {
                if (!_stages.TryGet(preparedStage.StageId, out var stage))
                {
                    throw new InvalidOperationException(
                        $"Package store stage '{preparedStage.StageId}' disappeared before durable preparation.");
                }
                try
                {
                    await _transactions.PrepareAsync(stage, cancellationToken).ConfigureAwait(false);
                }
                catch (PackageStoreSimulatedCrashException)
                {
                    throw;
                }
                catch
                {
                    try
                    {
                        await _stages.DiscardAsync(preparedStage.StageId).ConfigureAwait(false);
                        localBindingAuthority = null;
                    }
                    finally
                    {
                        await _transactions.DiscardPreparedAsync(preparedStage.StageId).ConfigureAwait(false);
                    }
                    throw;
                }
            }
            return preparation;
        }
        catch
        {
            if (localBindingAuthority is not null)
            {
                await localBindingAuthority!.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<PackageOperationResult> CommitStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
        => (await CommitStageWithOutcomeAsync(stageId, cancellationToken).ConfigureAwait(false)).OperationResult;

    internal async Task<PackageStoreCommitResult> CommitStageWithOutcomeAsync(
        string stageId,
        CancellationToken cancellationToken = default,
        bool retainCutoverForPublication = false,
        Action? durableCommitStarted = null,
        bool packagePolicyFenceHeld = false)
    {
        if (!packagePolicyFenceHeld && _packagePolicies is not null && _stages.TryGet(stageId, out var expected)
            && expected.PackagePolicyAuthority is { Count: > 0 } authority)
            return await _packagePolicies.ExecuteUnderAuthorityFenceAsync(authority,
                token => CommitStageWithOutcomeAsync(stageId, token, retainCutoverForPublication, durableCommitStarted, packagePolicyFenceHeld: true), cancellationToken);
        if (!_stages.TryTake(stageId, out var stage))
        {
            return new PackageStoreCommitResult(
                PackageOperationResults.Failure($"Package store stage '{stageId}' was not found."),
                PackageStoreCommitDisposition.RolledBack);
        }

        if (stage.BaseCatalogGeneration != CatalogGeneration)
        {
            try
            {
                PackageStoreStageManager.Cleanup(stage.PreparedArchives);
                if (stage.LocalBindingAuthority is not null)
                {
                    await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await _transactions.DiscardPreparedAsync(stageId).ConfigureAwait(false);
            }
            return new PackageStoreCommitResult(
                PackageOperationResults.Failure(
                    $"Package store stage '{stageId}' is stale because the installed package catalog changed before commit."),
                PackageStoreCommitDisposition.RolledBack);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            try
            {
                PackageStoreStageManager.Cleanup(stage.PreparedArchives);
                if (stage.LocalBindingAuthority is not null)
                {
                    await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await _transactions.DiscardPreparedAsync(stageId).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        PackageStoreCommitResult result;
        if (!retainCutoverForPublication)
        {
            try
            {
                await AdvancePreparedCutoverToPreCommitAsync(stageId).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                PackageStoreStageManager.Cleanup(stage.PreparedArchives);
                if (stage.LocalBindingAuthority is not null)
                {
                    await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
                }
                return new PackageStoreCommitResult(
                    PackageOperationResults.Failure(
                        $"Failed to commit package store transaction '{stageId}': {exception.Message}"),
                    PackageStoreCommitDisposition.RolledBack);
            }
        }
        try
        {
            if (retainCutoverForPublication)
            {
                await _transactions.MarkRuntimePublicationRequiredAsync(stageId).ConfigureAwait(false);
            }
            result = await _transactions.CommitAsync(stage, cancellationToken, durableCommitStarted);
            if (result.Disposition == PackageStoreCommitDisposition.Committed
                && !retainCutoverForPublication)
            {
                await _transactions.AdvanceCutoverPhaseAsync(
                    stageId,
                    PackageStoreCutoverPhase.GenerationCommitAcknowledged).ConfigureAwait(false);
                await _transactions.AdvanceCutoverPhaseAsync(
                    stageId,
                    PackageStoreCutoverPhase.OldGenerationStopped).ConfigureAwait(false);
                var completionWarnings = await _transactions.CompleteCutoverAsync(stageId)
                    .ConfigureAwait(false);
                if (completionWarnings.Count > 0)
                {
                    result = result with
                    {
                        OperationResult = result.OperationResult with
                        {
                            Warnings = result.OperationResult.Warnings.Concat(completionWarnings).ToArray(),
                        },
                    };
                }
            }
        }
        catch
        {
            await AdvanceCatalogGenerationIfDesiredCatalogIsAuthoritativeAsync(stage)
                .ConfigureAwait(false);
            if (stage.LocalBindingAuthority is not null)
            {
                await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        if (result.Disposition == PackageStoreCommitDisposition.Committed)
        {
            Interlocked.Increment(ref _catalogGeneration);
        }
        if (stage.LocalBindingAuthority is not null)
        {
            await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
        }

        return result;
    }

    internal Task AdvanceCutoverPhaseAsync(
        string stageId,
        PackageStoreCutoverPhase phase) =>
        _transactions.AdvanceCutoverPhaseAsync(stageId, phase);

    internal Task<IReadOnlyList<string>> CompleteCutoverAsync(string stageId) =>
        _transactions.CompleteCutoverAsync(stageId);

    internal Task PrepareStorageActivationAsync(
        string stageId,
        IReadOnlyList<string> packageIds) =>
        _transactions.PrepareStorageActivationAsync(stageId, packageIds);

    internal async Task<PackageStoreTransactionJournal?> GetPendingRuntimeCutoverAsync(
        CancellationToken cancellationToken) =>
        (await _recovery.ReadPendingJournalsAsync(cancellationToken).ConfigureAwait(false))
        .SingleOrDefault(static journal =>
            journal.RequiresRuntimePublication
            && journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
            && journal.CutoverPhase < PackageStoreCutoverPhase.Completed);

    internal async Task<IReadOnlyList<string>> CompleteRecoveredCutoversAsync(
        CancellationToken cancellationToken,
        bool acknowledgeEmptyGeneration = false)
    {
        var warnings = new List<string>();
        var authoritativeCatalog = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var journal in (await _recovery.ReadPendingJournalsAsync(cancellationToken).ConfigureAwait(false))
                     .Where(static journal =>
                         journal.RequiresRuntimePublication
                         && journal.CutoverPhase >= PackageStoreCutoverPhase.DurableCommitted
                         && journal.CutoverPhase < PackageStoreCutoverPhase.Completed))
        {
            if (journal.CutoverPhase < PackageStoreCutoverPhase.DurableCommitted)
            {
                throw new InvalidDataException(
                    $"Package store transaction '{journal.TransactionId}' was not recovered to a durable cutover boundary.");
            }
            if (!PackageStorePolicy.CatalogsEqual(authoritativeCatalog, journal.DesiredPackages))
            {
                throw new InvalidDataException(
                    $"Package store transaction '{journal.TransactionId}' cannot complete because the authoritative catalog no longer matches its desired state.");
            }
            if (journal.CutoverPhase < PackageStoreCutoverPhase.GenerationCommitAcknowledged)
            {
                if (!acknowledgeEmptyGeneration)
                {
                    throw new InvalidOperationException(
                        $"Package store transaction '{journal.TransactionId}' has not acknowledged its recovered Runtime generation.");
                }
                await _transactions.AdvanceCutoverPhaseAsync(
                    journal.TransactionId,
                    PackageStoreCutoverPhase.GenerationCommitAcknowledged).ConfigureAwait(false);
            }
            if (journal.CutoverPhase < PackageStoreCutoverPhase.OldGenerationStopped)
            {
                if (!acknowledgeEmptyGeneration)
                {
                    throw new InvalidOperationException(
                        $"Package store transaction '{journal.TransactionId}' has not stopped its previous Runtime generation.");
                }
                await _transactions.AdvanceCutoverPhaseAsync(
                    journal.TransactionId,
                    PackageStoreCutoverPhase.OldGenerationStopped).ConfigureAwait(false);
            }
            warnings.AddRange(await _transactions.CompleteCutoverAsync(journal.TransactionId)
                .ConfigureAwait(false));
        }
        return warnings;
    }

    private async Task AdvancePreparedCutoverToPreCommitAsync(string stageId)
    {
        await _transactions.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreLaunchPolicyConfirmed).ConfigureAwait(false);
        await _transactions.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.CandidateReady).ConfigureAwait(false);
        await _transactions.AdvanceCutoverPhaseAsync(
            stageId,
            PackageStoreCutoverPhase.PreCommitPolicyConfirmed).ConfigureAwait(false);
    }

    private async Task AdvanceCatalogGenerationIfDesiredCatalogIsAuthoritativeAsync(
        PendingStoreStage stage)
    {
        if (PackageStorePolicy.CatalogsEqual(stage.PreviousPackages, stage.DesiredPackages))
        {
            return;
        }

        try
        {
            var catalog = await _store.ListAsync(CancellationToken.None).ConfigureAwait(false);
            if (PackageStorePolicy.CatalogsEqual(catalog, stage.DesiredPackages))
            {
                Interlocked.CompareExchange(
                    ref _catalogGeneration,
                    checked(stage.BaseCatalogGeneration + 1),
                    stage.BaseCatalogGeneration);
            }
        }
        catch
        {
            // Preserve the original commit exception when the durable catalog cannot be verified.
        }
    }

    public async Task<bool> DiscardStageAsync(string stageId)
    {
        var discarded = false;
        try
        {
            discarded = await _stages.DiscardAsync(stageId).ConfigureAwait(false);
            return discarded;
        }
        finally
        {
            if (discarded)
            {
                await _transactions.DiscardPreparedAsync(stageId).ConfigureAwait(false);
            }
        }
    }

    public async Task<PackageOperationResult> ExecuteAsync(
        IReadOnlyList<PackageStoreChange> mutations,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareStageAsync(mutations, cancellationToken);
        return preparation.Stage is null
            ? preparation.Failure ?? PackageOperationResults.Failure("Package store transaction could not be prepared.")
            : await CommitStageAsync(preparation.Stage.StageId, cancellationToken);
    }

    public async Task DiscardAllStagesAsync()
    {
        var stageIds = await _stages.DiscardAllAsync().ConfigureAwait(false);
        foreach (var stageId in stageIds)
        {
            await _transactions.DiscardPreparedAsync(stageId).ConfigureAwait(false);
        }
    }

    internal async Task<ExecutionRuntimeGarbageCollectionAuthoritySnapshot>
        CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(CancellationToken cancellationToken)
    {
        var packages = new List<InstalledPackageRecord>();
        packages.AddRange(await _store.ListAsync(cancellationToken).ConfigureAwait(false));
        packages.AddRange(_stages.CaptureGarbageCollectionAuthority());
        foreach (var journal in await _recovery.ReadPendingJournalsAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.AddRange(journal.PreviousPackages);
            packages.AddRange(journal.DesiredPackages);
        }

        return new ExecutionRuntimeGarbageCollectionAuthoritySnapshot(
            packages.SelectMany(static package => package.ExecutionRuntimeBindings));
    }

    internal long CatalogGeneration => Interlocked.Read(ref _catalogGeneration);

    internal RuntimePackagePaths Paths => _paths;
}
