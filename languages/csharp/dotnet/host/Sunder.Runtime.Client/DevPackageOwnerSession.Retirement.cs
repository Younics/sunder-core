using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class DevPackageOwnerSession
{
    private DevPackageOwnerReleaseStageRequest? _pendingRelease;
    private string? _retiredReleaseStage;
    private bool _releaseCommitPending;
    private Task? _pendingRetirementCallback;
    private string? _pendingRetirementStage;

    public async Task<RuntimePackageSnapshot?> ReleaseStagedAsync(
        Func<PackageLifecycleStageResult, CancellationToken, Task> retireActivations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retireActivations);
        using var timeout = new CancellationTokenSource(_options.ReleaseTimeout);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var entered = false;
        try
        {
            await _mutationGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
            entered = true;
            using var client = _clients();
            DevPackageOwnerLeaseResponse? lease;
            lock (_syncRoot) lease = _lease;
            if (lease is null && _pendingMutation is null) return null;
            var instance = _pendingMutation?.RuntimeInstanceId ?? lease!.RuntimeInstanceId;
            lease = await ResolvePendingMutationAsync(client, instance, cleanup.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Development owner release has no acknowledged lease.");
            if (_pendingRelease is null && GetDesiredFolders() is { } folders && folders.Any(folder => folder.Watch))
            {
                // A queued output-watch commit must finish or be withdrawn before
                // preparing App retirement. This is exact-owner cleanup, not a new
                // acquisition or a package capability that revocation can reopen.
                // Keep a lost response pinned to this same mutation/revision and
                // prevent another acquisition until release is acknowledged.
                _releaseCommitPending = true;
                var quiet = CreateMutation(instance, checked(lease.Revision + 1),
                    folders.Select(folder => folder with { Watch = false }).ToArray());
                // Publish release intent before I/O, so a rejected heartbeat
                // cannot recreate this owner while the quieting reply is lost.
                lock (_syncRoot) _pendingRelease = new(instance, OwnerToken, quiet.Revision, Guid.NewGuid().ToString("N"));
                SetPendingMutation(quiet);
                var observed = await client.ReplaceDevPackageOwnerAsync(OwnerId, quiet, cleanup.Token).ConfigureAwait(false);
                CommitMutation(quiet, observed);
                lease = observed;
            }
            lock (_syncRoot) _pendingRelease ??= new(instance, OwnerToken, lease.Revision, Guid.NewGuid().ToString("N"));
            if (_pendingRelease.RuntimeInstanceId != instance || _pendingRelease.Revision != lease.Revision)
                throw new InvalidOperationException("The pending development owner release belongs to another lease revision.");
            var stage = await client.StageDevPackageOwnerReleaseAsync(OwnerId, _pendingRelease, cleanup.Token).ConfigureAwait(false);
            if (!stage.Success || stage.StageId is null || stage.BaseStamp?.RuntimeInstanceId != instance)
                throw new InvalidDataException("Runtime did not prepare the exact development owner release.");
            if (_retiredReleaseStage != stage.StageId)
            {
                if (_pendingRetirementCallback is { IsCompleted: false } && _pendingRetirementStage != stage.StageId)
                    throw new InvalidOperationException("The previous development retirement callback is still running.");
                if (_pendingRetirementCallback is null || _pendingRetirementCallback.IsFaulted || _pendingRetirementCallback.IsCanceled
                    || _pendingRetirementStage != stage.StageId)
                {
                    _pendingRetirementStage = stage.StageId;
                    _pendingRetirementCallback = retireActivations(stage, cleanup.Token);
                }
                await _pendingRetirementCallback.WaitAsync(cleanup.Token).ConfigureAwait(false);
                _retiredReleaseStage = stage.StageId;
                _pendingRetirementCallback = null;
                _pendingRetirementStage = null;
            }
            _releaseCommitPending = true;
            var result = await client.CommitDevPackageOwnerReleaseAsync(OwnerId,
                new(instance, OwnerToken, stage.StageId), cleanup.Token).ConfigureAwait(false);
            if (result.RuntimeInstanceId != instance || result.OwnerId != OwnerId || result.StageId != stage.StageId
                || result.Snapshot.RuntimeInstanceId != instance)
                throw new InvalidDataException("Runtime returned a mismatched development owner release acknowledgement.");
            ClearOwnershipState(instance);
            _pendingRelease = null;
            _retiredReleaseStage = null;
            _releaseCommitPending = false;
            // Heartbeat remains live throughout shell cleanup; stop only after the
            // server has acknowledged withdrawal. Do not await a heartbeat trying
            // to enter this same mutation gate.
            DetachReleasedHeartbeat();
            return result.Snapshot;
        }
        catch (RuntimeClientException error) when (error.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.Unauthorized)
        {
            // A definite server rejection is not a withdrawal acknowledgement.
            // A later explicit attempt prepares against the newly observed graph.
            _pendingRelease = null;
            _retiredReleaseStage = null;
            _releaseCommitPending = false;
            throw;
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Acknowledged development owner release exceeded its cleanup budget.", error);
        }
        finally
        {
            if (entered) _mutationGate.Release();
        }
    }

    private void DetachReleasedHeartbeat()
    {
        lock (_syncRoot)
        {
            var cancellation = _heartbeatCancellation;
            var task = _heartbeatTask;
            _heartbeatCancellation = null;
            _heartbeatTask = Task.CompletedTask;
            if (cancellation is null) return;
            cancellation.Cancel();
            _ = DisposeReleasedHeartbeatAsync(task, cancellation);
        }
    }

    private static async Task DisposeReleasedHeartbeatAsync(Task task, CancellationTokenSource cancellation)
    {
        try { await task.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
}
