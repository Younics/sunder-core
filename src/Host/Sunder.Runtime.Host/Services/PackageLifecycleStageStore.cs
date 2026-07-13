namespace Sunder.Runtime.Host.Services;

internal sealed class PackageLifecycleStageStore
{
    private readonly Dictionary<string, PendingPackageLifecycleStage> _stages = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string stageId, PendingPackageLifecycleStage stage) => _stages.Add(stageId, stage);

    public bool TryTake(string stageId, out PendingPackageLifecycleStage stage)
        => _stages.Remove(stageId, out stage!);

    public async Task DisposeAllAsync()
    {
        foreach (var stage in _stages.Values) await stage.Session.DisposeAsync();
        _stages.Clear();
    }
}

internal sealed record PendingPackageLifecycleStage(
    ActivePackageSession Session,
    PackageSessionSourceSnapshot Sources,
    IReadOnlyList<string> ImpactedPackageIds,
    long BaseGeneration);
