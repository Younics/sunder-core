using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeEventStreamService
{
    internal const int ReplayCapacity = 256;
    internal const int SubscriberCapacity = 64;

    private readonly object _stateGate = new();
    private readonly BoundedReplayFeed<RuntimeEventDescriptor> _feed = new(ReplayCapacity, SubscriberCapacity);
    private long _sessionGeneration;
    private RuntimeOperationPhase _operationPhase;
    private IReadOnlyList<string> _activePackageIds = [];

    public RuntimeEventSnapshot GetSnapshot(long afterSequenceId = 0)
    {
        var replay = _feed.Snapshot(afterSequenceId, ReplayCapacity);
        lock (_stateGate)
        {
            return new RuntimeEventSnapshot(
                replay.SequenceId,
                _sessionGeneration,
                _operationPhase,
                _activePackageIds,
                replay.Items,
                replay.HistoryGap);
        }
    }

    public BoundedReplayFeed<RuntimeEventDescriptor>.ReplayFeedSubscription<RuntimeEventDescriptor> Subscribe(long afterSequenceId)
        => _feed.Subscribe(afterSequenceId, sequenceId =>
        {
            lock (_stateGate)
            {
                return CreateEvent(
                    sequenceId,
                    RuntimeEventKind.Snapshot,
                    _sessionGeneration,
                    _operationPhase,
                    _activePackageIds);
            }
        });

    public void PublishSessionGeneration(long generation, IReadOnlyList<string> activePackageIds)
    {
        var normalizedIds = NormalizePackageIds(activePackageIds);
        RuntimeOperationPhase phase;
        lock (_stateGate)
        {
            _sessionGeneration = generation;
            _activePackageIds = normalizedIds;
            phase = _operationPhase;
        }

        _feed.Publish(sequenceId => CreateEvent(
            sequenceId,
            RuntimeEventKind.SessionGenerationChanged,
            generation,
            phase,
            normalizedIds));
    }

    public void PublishOperationPhase(RuntimeOperationPhase phase, IReadOnlyList<string>? packageIds = null)
    {
        long generation;
        lock (_stateGate)
        {
            _operationPhase = phase;
            generation = _sessionGeneration;
        }

        _feed.Publish(sequenceId => CreateEvent(
            sequenceId,
            RuntimeEventKind.OperationPhaseChanged,
            generation,
            phase,
            NormalizePackageIds(packageIds ?? [])));
    }

    public void PublishDevReloadResult(bool success, IReadOnlyList<string> packageIds, string? message)
    {
        long generation;
        RuntimeOperationPhase phase;
        lock (_stateGate)
        {
            generation = _sessionGeneration;
            phase = _operationPhase;
        }

        _feed.Publish(sequenceId => CreateEvent(
            sequenceId,
            RuntimeEventKind.DevReloadCompleted,
            generation,
            phase,
            NormalizePackageIds(packageIds),
            success,
            NormalizeMessage(message)));
    }

    public void Complete()
    {
        lock (_stateGate)
        {
            _operationPhase = RuntimeOperationPhase.ShuttingDown;
        }

        _feed.Complete();
    }

    private static RuntimeEventDescriptor CreateEvent(
        long sequenceId,
        RuntimeEventKind kind,
        long generation,
        RuntimeOperationPhase phase,
        IReadOnlyList<string> packageIds,
        bool? success = null,
        string? message = null)
        => new(sequenceId, DateTimeOffset.UtcNow, kind, generation, phase, packageIds, success, message);

    private static IReadOnlyList<string> NormalizePackageIds(IEnumerable<string> packageIds)
        => packageIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 512 ? singleLine : singleLine[..512];
    }
}
