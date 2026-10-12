using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class InstalledPackageLifecycleService
{
    internal IReadOnlyList<string> RuntimeRestartScope => _sessions.State.GetActivePackages().Select(value => value.PackageId).Order(StringComparer.Ordinal).ToArray();

    // Called only while RuntimeOperationGate is held. Stopped updates use the existing journal
    // without publishing/restarting the unrelated live session. Manual restart uses its normal candidate flow.
    internal async Task<PackageOperationResult> ApplyRuntimeBindingsUnderGateAsync(
        IReadOnlyList<PackageExecutionRuntimeRebindMutation> changes, bool restart,
        CancellationToken token,
        Func<CancellationToken, Task>? confirmCurrent = null)
    {
        if (_stages.Count != 0 || _storeCoordinator.HasPreparedStages)
            throw new RuntimeConflictException("A package transaction is already pending.");
        var preparation = await _storeCoordinator.PrepareStageAsync(changes.Cast<PackageStoreChange>().ToArray(), null, token);
        if (preparation.Stage is null) return preparation.Failure ?? PackageOperationResults.Failure("Runtime binding preparation failed.");
        try
        {
            if (!restart)
            {
                var commit = await _storeCoordinator.ExecuteUnderPackagePolicyFenceAsync(preparation.Stage.StageId, async fencedToken =>
                {
                    if (confirmCurrent is not null) await confirmCurrent(fencedToken);
                    return await _storeCoordinator.CommitStageWithOutcomeAsync(preparation.Stage.StageId, fencedToken, packagePolicyFenceHeld: true);
                }, token);
                if (commit.Disposition == PackageStoreCommitDisposition.Indeterminate) _gate.FenceForIntegrityFailure();
                return commit.OperationResult with { StoreCommitted = commit.Disposition == PackageStoreCommitDisposition.Committed,
                    RuntimeSessionApplied = false, CommittedStamp = _sessions.Stamp,
                    RuntimeSessionReconciliationPending = commit.Disposition == PackageStoreCommitDisposition.Indeterminate };
            }
            var stage = await PrepareRuntimeBindingCandidateUnderGateAsync(preparation, token);
            return stage.StageId is null ? stage.OperationResult : await CommitStageCoreAsync(stage.StageId,
                confirmCurrent is null ? null : (_, currentToken) => confirmCurrent(currentToken), token);
        }
        finally
        {
            // DiscardStageAsync removes only a stage still owned by the stage manager.
            // Once commit takes ownership it is a no-op, preserving committed/indeterminate
            // journals. This also covers cancellation or policy failure before that take.
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
        }
    }

    internal Task<PackageStoreStageResult> PrepareRuntimeBindingCandidateUnderGateAsync(
        PackageStoreStagePreparation preparation, CancellationToken token)
        => PrepareCandidateAsync(preparation, _sessions.Generation, _sessions.State.GetActivePackages(), _sessions.Sources.Snapshot(), token);
}

internal sealed class ExecutionRuntimeActivationUpdates(Func<CancellationToken, Task> apply, ILogger<ExecutionRuntimeActivationUpdates> logger)
{
    internal async Task BeforeActivationAsync(CancellationToken token)
    {
        try { await apply(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed optional binary transition does not bypass or replace ordinary launch policy.
            logger.LogWarning("Prepared runtime update remains deferred before activation ({FailureType}).", error.GetType().Name);
        }
    }
}
