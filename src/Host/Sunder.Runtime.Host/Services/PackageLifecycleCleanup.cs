namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    public async Task ShutdownStagesAsync()
    {
        foreach (var stage in _stages.TakeAll())
        {
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
    public Task ShutdownAsync(PackageSessionLifecycleService sessionLifecycle)
        => _gate.ShutdownAsync(async () =>
        {
            await RunShutdownStepAsync("discard lifecycle stages", sessionLifecycle.ShutdownStagesAsync);
            var storeStages = _stages.ToArray();
            _stages.Clear();
            foreach (var (stageId, stage) in storeStages)
            {
                if (stage.Candidate is not null)
                {
                    await RunShutdownStepAsync(
                        $"discard package store stage '{stageId}'",
                        () => _publisher.DiscardAsync(stage.Candidate));
                }
            }
            _reconciliationPendingStages.Clear();
            _sessions.ClearStages();
            await RunShutdownStepAsync("discard durable package store stages", _storeCoordinator.DiscardAllStagesAsync);
            try
            {
                _sessions.Callbacks.Clear();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clear package callback sessions during Runtime shutdown");
            }
            await RunShutdownStepAsync("retire the active package session", () => _sessions.State.ClearActiveSessionAsync());
            try
            {
                RuntimePackageSessionDirectories.CleanupStaleSessions();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clean stale package session directories during Runtime shutdown");
            }
        });

    private async Task RunShutdownStepAsync(string operation, Func<Task> action)
    {
        try
        {
            await action().WaitAsync(_lifecyclePolicy.ShutdownCleanupStepTimeout);
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
