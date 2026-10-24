namespace Sunder.Runtime.Host.Services;

internal sealed class PackageLifecycleStageStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingPackageLifecycleStage> _stages = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string stageId, PendingPackageLifecycleStage stage)
    {
        if (!_stages.TryAdd(stageId, stage)) throw new InvalidOperationException("Duplicate lifecycle stage.");
    }

    internal PendingPackageLifecycleStage? Get(string stageId) => _stages.GetValueOrDefault(stageId);

    public int Count => _stages.Count;

    public bool TryTake(string stageId, out PendingPackageLifecycleStage stage)
        => _stages.TryRemove(stageId, out stage!);

    public IReadOnlyList<PendingPackageLifecycleStage> TakeAll()
    {
        var stages = _stages.Values.ToArray();
        _stages.Clear();
        return stages;
    }

    public IReadOnlyList<PendingPackageLifecycleStage> TakeExpiredOrStale(
        DateTimeOffset now,
        long generation)
    {
        var stages = _stages
            .Where(pair => pair.Value.ExpiresAtUtc <= now || pair.Value.BaseGeneration != generation)
            .Select(pair => pair.Key)
            .ToArray();
        var removed = new List<PendingPackageLifecycleStage>(stages.Length);
        foreach (var stageId in stages)
        {
            if (_stages.TryRemove(stageId, out var stage))
            {
                removed.Add(stage);
            }
        }
        return removed;
    }

    internal PreparedPackageSession? GetCandidate(string stageId)
        => _stages.TryGetValue(stageId, out var stage) ? stage.Candidate : null;
}

internal sealed record PendingPackageLifecycleStage(
    PreparedPackageSession Candidate,
    IReadOnlyList<string> ImpactedPackageIds,
    long BaseGeneration,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    internal string? ReleaseOwnerId { get; init; }
    internal string? ReloadOwnerId { get; init; }
    internal bool SourcesOnly { get; init; }
}
