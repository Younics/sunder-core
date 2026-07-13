using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSessionOwner
{
    private readonly RuntimeEventStreamService _events;
    private readonly ConcurrentDictionary<string, long> _stageGenerations = new(StringComparer.Ordinal);

    public RuntimeSessionOwner(
        ILogger<RuntimeSessionOwner> logger,
        RuntimeEventStreamService events,
        RuntimeAuthPolicyOptions? authPolicy = null,
        RuntimePackageOperationPolicyOptions? packageOperationPolicy = null,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? hostLifetime = null)
    {
        _events = events;
        PackageCallbackSessionCoordinator? callbacks = null;
        State = new PackageSessionState(
            logger,
            () => callbacks?.Clear(),
            packageId => callbacks?.RemovePackageSessions(packageId),
            packageOperationPolicy?.SessionDrainTimeout);
        Callbacks = callbacks = new PackageCallbackSessionCoordinator(
            State,
            authPolicy,
            timeProvider,
            hostLifetime?.ApplicationStopping ?? CancellationToken.None);
        Auth = new PackageAuthSessionCoordinator(
            State,
            Callbacks,
            (packageId, generation, origin, exception, action) =>
                State.HandlePackageFault(packageId, generation, origin, exception, action));
    }

    public PackageSessionState State { get; }

    public PackageSessionSourceState Sources { get; } = new();

    public PackageAuthSessionCoordinator Auth { get; }

    public PackageCallbackSessionCoordinator Callbacks { get; }

    public long Generation => State.Generation;

    public async Task<IReadOnlyList<string>> PublishAsync(
        ActivePackageSession session,
        CancellationToken cancellationToken = default)
    {
        var publication = await State.PublishSessionAsync(session, cancellationToken);
        _events.PublishSessionGeneration(
            publication.Generation,
            session.GetActivePackages().Select(package => package.PackageId).ToArray());
        return publication.Warnings;
    }

    public void RegisterStage(string stageId, long generation) => _stageGenerations[stageId] = generation;

    public bool TryGetStageGeneration(string stageId, out long generation)
        => _stageGenerations.TryGetValue(stageId, out generation);

    public void RemoveStage(string stageId) => _stageGenerations.TryRemove(stageId, out _);

    public void ClearStages() => _stageGenerations.Clear();
}
