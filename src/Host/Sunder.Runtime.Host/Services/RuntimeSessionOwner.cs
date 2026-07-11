using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSessionOwner
{
    private readonly RuntimeEventStreamService _events;
    private readonly ConcurrentDictionary<string, long> _stageGenerations = new(StringComparer.Ordinal);

    public RuntimeSessionOwner(ILogger<RuntimeSessionOwner> logger, RuntimeEventStreamService events)
    {
        _events = events;
        PackageAuthSessionCoordinator? auth = null;
        State = new PackageSessionState(
            logger,
            () => auth?.Clear(),
            packageId => auth?.RemovePackageSessions(packageId));
        Auth = auth = new PackageAuthSessionCoordinator(
            State.GetLoadedPackageLease,
            (packageId, generation, origin, exception, action) =>
                State.HandlePackageFault(packageId, generation, origin, exception, action));
    }

    public PackageSessionState State { get; }

    public PackageSessionSourceState Sources { get; } = new();

    public PackageAuthSessionCoordinator Auth { get; }

    public long Generation => State.Generation;

    public long Publish(ActivePackageSession session)
    {
        var generation = State.PublishSession(session);
        _events.PublishSessionGeneration(
            generation,
            session.GetActivePackages().Select(package => package.PackageId).ToArray());
        return generation;
    }

    public void RegisterStage(string stageId, long generation) => _stageGenerations[stageId] = generation;

    public bool TryGetStageGeneration(string stageId, out long generation)
        => _stageGenerations.TryGetValue(stageId, out generation);

    public void RemoveStage(string stageId) => _stageGenerations.TryRemove(stageId, out _);

    public void ClearStages() => _stageGenerations.Clear();
}
