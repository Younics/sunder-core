using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ExecutionRuntimeUpdateService
{
    private Task RequireCurrentAuthoritiesAsync(RuntimeExecutionRuntimePreparedUpdate update, CancellationToken token)
        => RequireCurrentAuthoritiesAsync(update.Runtimes, token);

    private async Task RequireCurrentAuthoritiesAsync(IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> runtimes, CancellationToken token)
    {
        var receipts = await store.ListReceiptsAsync(token).ConfigureAwait(false);
        foreach (var candidate in runtimes)
        {
            ValidateIdentity(candidate.CurrentRuntime);
            var current = receipts.FirstOrDefault(receipt => receipt.Identity.ToContract() == candidate.CurrentRuntime);
            if (current is null) throw new ExecutionRuntimeCurrentAuthorityException(ExecutionRuntimeErrorCodes.Missing);
            var verification = await store.VerifyAsync(current.Identity, deep: false, token).ConfigureAwait(false);
            if (!verification.Healthy) throw new ExecutionRuntimeCurrentAuthorityException(ExecutionRuntimeErrorCodes.IntegrityFailed);
        }
    }

    // Explicit/background network boundary. Startup/next-activation never calls this.
    private async Task<RuntimeExecutionRuntimePreparedUpdate> RefreshPreparedAuthorityAsync(RuntimeExecutionRuntimePreparedUpdate pending, CancellationToken token)
    {
        var candidates = new List<RuntimeExecutionRuntimeUpdateCandidate>();
        foreach (var candidate in pending.Runtimes)
        {
            var refreshed = await installation.ResolveAsync(candidate.Resolution.Requirement, token, candidate.CurrentRuntime,
                candidate.Resolution.ResolutionFingerprint).ConfigureAwait(false);
            if (refreshed.Artifact.Identity != candidate.Resolution.Artifact.Identity) throw Stale();
            await installation.ReconfirmAsync(refreshed, token, candidate.CurrentRuntime).ConfigureAwait(false);
            candidates.Add(candidate with { Resolution = refreshed });
        }
        var changes = pending.BindingChanges.Select(change => change with
        {
            Resolution = candidates.First(candidate => candidate.CurrentRuntime == change.PreviousBinding.Runtime
                && candidate.Resolution.Artifact.Identity == change.Resolution.Artifact.Identity).Resolution,
        }).ToArray();
        return new(pending.UpdateId, Hash(new { Runtimes = candidates, BindingChanges = changes }), pending.State, pending.PreparedAtUtc, candidates, changes);
    }

    private async Task<string?> RefreshPendingUpdatesAsync(ExecutionRuntimeManagementDocument management, CancellationToken token)
    {
        string? failure = null;
        foreach (var pending in management.Updates.Where(ExecutionRuntimeManagementState.Pending))
        {
            var installedNow = await installed.ListAsync(token).ConfigureAwait(false);
            if (pending.BindingChanges.Count > 0 && pending.BindingChanges.All(change => installedNow.Any(package => package.PackageId == change.PackageId
                && package.Version == change.PackageVersion && package.ContentIdentity == change.PackageContentIdentity
                && package.ExecutionRuntimeBindings.Any(binding => Matches(binding, change.Resolution.Artifact.Identity, change.PreviousBinding.Role)))))
                continue;

            RuntimeExecutionRuntimePreparedUpdate? refreshed = null;
            Exception? rejected = null;
            try { refreshed = await RefreshPreparedAuthorityAsync(pending, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { rejected = error; }
            await using var gate = await operations.EnterAsync(token).ConfigureAwait(false);
            var current = await state.ReadAsync(gate.CancellationToken).ConfigureAwait(false);
            var same = current.Updates.SingleOrDefault(value => value.UpdateId == pending.UpdateId
                && value.Fingerprint == pending.Fingerprint
                && ExecutionRuntimeManagementState.Pending(value));
            if (!current.Settings.AutomaticUpdatesEnabled || same is null) continue;
            if (refreshed is not null)
                await state.SaveUpdateAsync(refreshed with { State = same.State }, gate.CancellationToken).ConfigureAwait(false);
            else if (rejected is not null)
            {
                var code = ExecutionRuntimeInstallationService.ErrorCode(rejected);
                failure ??= code;
                var outcome = rejected is ExecutionRuntimeCurrentAuthorityException
                    ? RuntimeExecutionRuntimeUpdateState.Blocked
                    : code == ExecutionRuntimeErrorCodes.PlanStale
                        ? RuntimeExecutionRuntimeUpdateState.Stale
                        : same.State;
                if (outcome != same.State) await state.SaveUpdateAsync(same with { State = outcome }, gate.CancellationToken).ConfigureAwait(false);
            }
        }
        return failure;
    }
}
