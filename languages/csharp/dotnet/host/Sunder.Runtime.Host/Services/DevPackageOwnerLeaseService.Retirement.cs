using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class DevPackageOwnerLeaseService
{
    private readonly Dictionary<string, ReleaseStage> _releaseStages = new(StringComparer.Ordinal);

    internal async Task<PackageLifecycleStageResult> StageReleaseAsync(string ownerId,
        DevPackageOwnerReleaseStageRequest request, CancellationToken token)
    {
        ValidateOwnerId(ownerId); ValidateToken(request.OwnerToken); ValidateRuntimeInstance(request.RuntimeInstanceId); ValidateMutationId(request.MutationId);
        await _gate.WaitAsync(token);
        try
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var expired in _releaseStages.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _releaseStages.Remove(expired);
            var previous = _releaseStages.Values.SingleOrDefault(value => value.Owner.OwnerId == ownerId && value.MutationId == request.MutationId);
            if (previous is not null)
            {
                Authenticate(previous.Owner, request.OwnerToken);
                if (previous.Owner.Revision != request.Revision) throw new RuntimeConflictException("Owner release mutation was already used with another revision.");
                return previous.Stage;
            }
            var lease = GetAuthenticated(ownerId, request.OwnerToken);
            if (lease.Revision != request.Revision || lease.ExpiresAtUtc <= now || !IsClientProcessAlive(lease))
                throw new RuntimeConflictException("Owner release targets a stale or expired lease.");
            if (_releaseStages.Count >= 32) throw new RuntimeUnavailableException("The Runtime owner release stage limit was reached.");
            var stage = await _sessions.StageOwnerReleaseAsync(ownerId, token);
            if (!stage.Success) throw new RuntimeConflictException("Owner release preparation failed.");
            _releaseStages.Add(stage.StageId!, new(lease, request.MutationId, stage, now + _policy.PendingStageLifetime));
            return stage;
        }
        finally { _gate.Release(); }
    }

    internal async Task<DevPackageOwnerReleaseResult> CommitReleaseAsync(string ownerId,
        DevPackageOwnerReleaseCommitRequest request, CancellationToken token)
    {
        ValidateOwnerId(ownerId); ValidateToken(request.OwnerToken); ValidateRuntimeInstance(request.RuntimeInstanceId);
        await _gate.WaitAsync(token);
        try
        {
            if (!_releaseStages.TryGetValue(request.StageId, out var stage) || stage.Owner.OwnerId != ownerId)
                throw new RuntimeAuthenticationException("Owner release stage credentials are invalid.");
            Authenticate(stage.Owner, request.OwnerToken);
            if (stage.Result is not null) return stage.Result;
            var lease = GetAuthenticated(ownerId, request.OwnerToken);
            if (lease.Revision != stage.Owner.Revision || lease.MutationId != stage.Owner.MutationId || lease.ExpiresAtUtc <= _timeProvider.GetUtcNow()
                || stage.ExpiresAtUtc <= _timeProvider.GetUtcNow() || !IsClientProcessAlive(lease))
                throw new RuntimeConflictException("Owner release stage is stale or expired.");
            var committed = await _sessions.CommitStageCoreAsync(request.StageId, ownerId, token);
            if (!committed.Success) throw new RuntimeConflictException(committed.Message ?? "Owner release was not committed.");
            _owners.Remove(ownerId);
            var result = new DevPackageOwnerReleaseResult(_protocol.RuntimeInstanceId, ownerId, request.StageId, _sessions.GetSnapshot());
            _releaseStages[request.StageId] = stage with { Result = result };
            // Publication and receipt are authoritative even if the response or
            // watcher reconciliation is interrupted. The same request is safe to query.
            await _watcher.SynchronizeAsync(_sessions.GetDevWatchTargets(), CancellationToken.None);
            return result;
        }
        finally { _gate.Release(); }
    }

    private sealed record ReleaseStage(OwnerLease Owner, string MutationId, PackageLifecycleStageResult Stage, DateTimeOffset ExpiresAtUtc)
    {
        internal DevPackageOwnerReleaseResult? Result { get; init; }
    }
}
