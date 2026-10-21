using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    private PackageLifecycleStageResult RegisterPreparedStage(PreparedPackageSession candidate,
        IReadOnlyList<string> impacted, string? releaseOwnerId = null, bool sourcesOnly = false)
    {
        var stageId = candidate.StageId!;
        var now = _timeProvider.GetUtcNow();
        var expires = now + _lifecyclePolicy.PendingStageLifetime;
        _stages.Add(stageId, new(candidate, impacted, candidate.BaseGeneration, now, expires)
            { ReleaseOwnerId = releaseOwnerId, SourcesOnly = sourcesOnly });
        _sessions.RegisterStage(stageId, candidate.BaseGeneration + (sourcesOnly ? 0 : 1), candidate.BaseGeneration,
            RuntimePackageStageKind.PackageSession, now, expires);
        if (!sourcesOnly) _appPolicies?.RegisterRetirementStage(stageId, candidate.Session, candidate.AppSourceWithdrawal);
        return new(stageId, sourcesOnly ? _sessions.GetSnapshot().ActivePackages : candidate.Session.GetActivePackages(),
            candidate.Warnings, candidate.Errors, impacted)
        {
            BaseStamp = _sessions.Stamp,
            Retirements = sourcesOnly ? [] : _appPolicies?.RetirementRequirements(candidate.Session, candidate.AppSourceWithdrawal).Select(value => value with { StageId = stageId }).ToArray() ?? [],
        };
    }

    internal async Task<PackageLifecycleStageResult> StageOwnerReleaseAsync(string ownerId, CancellationToken token)
    {
        await using var operation = await _gate.EnterAsync(token);
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (_stages.Count >= MaxPendingStages) throw new RuntimeUnavailableException("The Runtime lifecycle stage limit was reached.");
        var sources = _sessions.Sources.Snapshot();
        var before = sources.ActiveDevOverlays;
        sources.RemoveDevOverlaysForAppOwner(ownerId);
        var stageId = Guid.NewGuid().ToString("N");
        var generation = _sessions.Generation;
        if (ActiveOverlaySetsEqual(before, sources.ActiveDevOverlays))
            return RegisterPreparedStage(new(ActivePackageSession.CreateEmpty(), sources, [], [], generation, stageId, false), [], ownerId, sourcesOnly: true);
        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, generation, operation.CancellationToken);
        if (loaded.Session is null) throw new RuntimePackageValidationException(loaded.Errors.FirstOrDefault() ?? "Owner removal could not be prepared.");
        try
        {
            var impacted = PackageSessionImpactAnalyzer.Compare(_sessions.State.GetActivePackages(), _sessions.State.GetActivePackageSources(),
                loaded.Session.GetActivePackages(), loaded.Session.GetActivePackageSources(), []);
            var candidate = _publisher.Prepare(loaded.Session, sources, loaded.Warnings.Concat(loaded.Errors).ToArray(), [], generation, stageId, retainWorkersExcept: [])
                with { AppSourceWithdrawal = true };
            return RegisterPreparedStage(candidate, impacted, ownerId);
        }
        catch { await loaded.Session.DisposeAsync(); throw; }
    }

    private async Task WaitForAppRetirementAsync(string stageId, CancellationToken token)
    {
        if (_appPolicies is null) return;
        while (_stages.Get(stageId) is { } stage && !stage.SourcesOnly && stage.ReleaseOwnerId is null)
        {
            if (stage.ExpiresAtUtc <= _timeProvider.GetUtcNow() || stage.BaseGeneration != _sessions.Generation
                || !_sessions.Sources.IsCurrent(stage.Candidate.Sources)) return;
            _appPolicies.RegisterRetirementStage(stageId, stage.Candidate.Session);
            if (_appPolicies.RetirementRequirements(stage.Candidate.Session).Count == 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, token);
        }
    }
}
