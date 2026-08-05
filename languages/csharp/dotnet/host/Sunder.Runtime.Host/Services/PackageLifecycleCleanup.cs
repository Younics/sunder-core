namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    public async Task ShutdownStagesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var stage in _stages.TakeAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _publisher.DiscardAsync(stage.Candidate);
            if (stage.Candidate.StageId is { } stageId) _sessions.MarkStageDiscarded(stageId);
        }
    }

    internal async Task SweepStagesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await SweepStagesCoreAsync(now);
    }

    private async Task SweepStagesCoreAsync(DateTimeOffset now)
    {
        foreach (var stage in _stages.TakeExpiredOrStale(now, _sessions.Generation))
        {
            await _publisher.DiscardAsync(stage.Candidate);
            if (stage.Candidate.StageId is not { } stageId) continue;
            _sessions.MarkStageFailed(
                stageId,
                stage.ExpiresAtUtc <= now
                    ? "The package lifecycle stage expired before commit."
                    : "The package lifecycle stage became stale before commit.");
        }
    }
}

internal sealed partial class InstalledPackageLifecycleService
{
    public Task ShutdownAsync(
        PackageSessionLifecycleService sessionLifecycle,
        CancellationToken cancellationToken = default)
        => _gate.ShutdownAsync(async shutdownToken =>
        {
            await RunShutdownStepAsync(
                "discard lifecycle stages",
                token => sessionLifecycle.ShutdownStagesAsync(token),
                shutdownToken);
            var storeStages = _stages.ToArray();
            _stages.Clear();
            foreach (var (stageId, stage) in storeStages)
            {
                if (shutdownToken.IsCancellationRequested)
                {
                    break;
                }
                if (stage.Candidate is not null)
                {
                    await RunShutdownStepAsync(
                        $"discard package store stage '{stageId}'",
                        _ => _publisher.DiscardAsync(stage.Candidate),
                        shutdownToken);
                }
            }
            _reconciliationPendingStages.Clear();
            _sessions.ClearStages();
            await RunShutdownStepAsync(
                "discard durable package store stages",
                _ => _storeCoordinator.DiscardAllStagesAsync(),
                shutdownToken);
            try
            {
                _sessions.Callbacks.Clear();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clear package callback sessions during Runtime shutdown");
            }
            await RunShutdownStepAsync(
                "retire the active package session",
                async token =>
                {
                    _rpcCatalog?.DeactivateAll(_sessions.Generation);
                    await _sessions.State.ClearActiveSessionAsync(token);
                },
                shutdownToken);
            try
            {
                RuntimePackageSessionDirectories.CleanupStaleSessions();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clean stale package session directories during Runtime shutdown");
            }
        }, cancellationToken);

    private async Task RunShutdownStepAsync(
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await action(cancellationToken).WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to {ShutdownOperation} during Runtime shutdown", operation);
        }
    }

    internal async Task SweepStagesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await SweepStagesCoreAsync(now);
    }

    private async Task SweepStagesCoreAsync(DateTimeOffset now)
    {
        foreach (var (stageId, stage) in _stages.Where(pair =>
                     pair.Value.ExpiresAtUtc <= now
                     || pair.Value.BaseSessionGeneration != _sessions.Generation
                     || pair.Value.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration).ToArray())
        {
            if (!_stages.Remove(stageId)) continue;
            if (stage.Candidate is not null) await _publisher.DiscardAsync(stage.Candidate);
            else _sessions.RemoveStage(stageId);
            await _storeCoordinator.DiscardStageAsync(stageId);
            _sessions.MarkStageFailed(
                stageId,
                stage.ExpiresAtUtc <= now
                    ? "The package store stage expired before commit."
                    : "The package store stage became stale before commit.");
        }
    }
}
