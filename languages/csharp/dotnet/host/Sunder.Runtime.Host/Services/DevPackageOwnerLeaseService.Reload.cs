using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class DevPackageOwnerLeaseService
{
    private readonly Dictionary<string, ReloadStage> _reloadStages = new(StringComparer.Ordinal);

    internal async Task<DevPackageOwnerReloadStageResult> StageReloadAsync(string ownerId,
        DevPackageOwnerReloadStageRequest request, CancellationToken token)
    {
        ValidateOwnerId(ownerId); ValidateToken(request.OwnerToken); ValidateRuntimeInstance(request.RuntimeInstanceId); ValidateMutationId(request.MutationId);
        if (string.IsNullOrWhiteSpace(request.PackageId) || !RegistryActivePolicyClient.Hash(request.SourceContentIdentity))
            throw new RuntimeValidationException("A development reload requires one package and its exact output content identity.");
        await _gate.WaitAsync(token);
        try
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var expired in _reloadStages.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _reloadStages.Remove(expired);
            var previous = _reloadStages.Values.SingleOrDefault(value => value.Owner.OwnerId == ownerId && value.Request.MutationId == request.MutationId);
            if (previous is not null)
            {
                Authenticate(previous.Owner, request.OwnerToken);
                if (previous.Request != request) throw new RuntimeConflictException("The reload mutation was already used with different inputs.");
                return previous.Prepared;
            }
            var owner = GetAuthenticated(ownerId, request.OwnerToken);
            if (owner.Revision != request.Revision || owner.ExpiresAtUtc <= now || !IsClientProcessAlive(owner)
                || !owner.Packages.Any(package => package.PackageId == request.PackageId))
                throw new RuntimeConflictException("Development reload targets a stale, expired or unrelated owner lease.");
            if (_reloadStages.Count >= 32) throw new RuntimeUnavailableException("The Runtime owner reload stage limit was reached.");
            var stage = await _sessions.StageOwnerReloadAsync(ownerId, request.PackageId, request.SourceContentIdentity, token);
            if (!stage.Success) throw new RuntimeConflictException("Development reload preparation failed; the active revision is retained.");
            var prepared = new DevPackageOwnerReloadStageResult(request.PackageId, request.SourceContentIdentity, stage);
            _reloadStages.Add(stage.StageId!, new(owner, request, prepared, now + _policy.PendingStageLifetime));
            return prepared;
        }
        finally { _gate.Release(); }
    }

    internal async Task<DevPackageOwnerReloadResult> CommitReloadAsync(string ownerId,
        DevPackageOwnerReloadCommitRequest request, CancellationToken token)
    {
        ValidateOwnerId(ownerId); ValidateToken(request.OwnerToken); ValidateRuntimeInstance(request.RuntimeInstanceId);
        await _gate.WaitAsync(token);
        try
        {
            var stage = RequireReloadStage(ownerId, request);
            if (stage.Result is not null) return stage.Result;
            var owner = GetAuthenticated(ownerId, request.OwnerToken);
            if (owner.Revision != stage.Owner.Revision || owner.MutationId != stage.Owner.MutationId
                || owner.ExpiresAtUtc <= _timeProvider.GetUtcNow() || stage.ExpiresAtUtc <= _timeProvider.GetUtcNow() || !IsClientProcessAlive(owner))
                throw new RuntimeConflictException("Development reload stage is stale or expired.");
            var committed = await _sessions.CommitStageCoreAsync(request.StageId, ownerId, token);
            if (!committed.Success) throw new RuntimeConflictException(committed.Message ?? "Development reload was not committed.");
            var snapshot = _sessions.GetSnapshot();
            var package = snapshot.SessionPackages.SingleOrDefault(value => value.PackageId == stage.Request.PackageId);
            if (package?.SourceContentIdentity != stage.Request.SourceContentIdentity)
                throw new InvalidDataException("The committed development output identity differs from the prepared revision.");
            var result = new DevPackageOwnerReloadResult(_protocol.RuntimeInstanceId, ownerId, request.StageId,
                stage.Request.PackageId, stage.Request.SourceContentIdentity, snapshot);
            _reloadStages[request.StageId] = stage with { Result = result };
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task DiscardReloadAsync(string ownerId, DevPackageOwnerReloadCommitRequest request, CancellationToken token)
    {
        ValidateOwnerId(ownerId); ValidateToken(request.OwnerToken); ValidateRuntimeInstance(request.RuntimeInstanceId);
        await _gate.WaitAsync(token);
        try
        {
            var stage = RequireReloadStage(ownerId, request);
            if (stage.Result is not null) throw new RuntimeConflictException("An already committed reload cannot be discarded.");
            await _sessions.DiscardStageAsync(request.StageId, token);
        }
        finally { _gate.Release(); }
    }

    private ReloadStage RequireReloadStage(string ownerId, DevPackageOwnerReloadCommitRequest request)
    {
        if (!_reloadStages.TryGetValue(request.StageId, out var stage) || stage.Owner.OwnerId != ownerId)
            throw new RuntimeAuthenticationException("Development reload stage credentials are invalid.");
        Authenticate(stage.Owner, request.OwnerToken);
        return stage;
    }

    private sealed record ReloadStage(OwnerLease Owner, DevPackageOwnerReloadStageRequest Request,
        DevPackageOwnerReloadStageResult Prepared, DateTimeOffset ExpiresAtUtc)
    {
        internal DevPackageOwnerReloadResult? Result { get; init; }
    }
}
