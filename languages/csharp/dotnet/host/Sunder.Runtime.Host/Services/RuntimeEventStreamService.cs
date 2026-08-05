using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeEventStreamService
{
    internal const int ReplayCapacity = 256;
    internal const int SubscriberCapacity = 64;

    private readonly object _stateGate = new();
    private readonly BoundedReplayFeed<RuntimeEventDescriptor> _feed = new(ReplayCapacity, SubscriberCapacity);
    private readonly Guid _runtimeInstanceId;
    private long _sessionGeneration;
    private RuntimeOperationPhase _operationPhase;
    private RuntimeBootstrapState _bootstrapState = RuntimeBootstrapState.Starting;
    private IReadOnlyList<string> _activePackageIds = [];

    public RuntimeEventStreamService(RuntimeProtocolDescriptor? protocol = null)
    {
        _runtimeInstanceId = protocol?.RuntimeInstanceId ?? Guid.NewGuid();
    }

    public Guid RuntimeInstanceId => _runtimeInstanceId;

    public RuntimeEventSnapshot GetSnapshot(long afterSequenceId = 0)
    {
        lock (_stateGate)
        {
            var replay = _feed.Snapshot(afterSequenceId, ReplayCapacity);
            return new RuntimeEventSnapshot(
                _runtimeInstanceId,
                replay.SequenceId,
                _sessionGeneration,
                _operationPhase,
                _bootstrapState,
                _activePackageIds,
                replay.Items,
                replay.HistoryGap);
        }
    }

    public BoundedReplayFeed<RuntimeEventDescriptor>.ReplayFeedSubscription<RuntimeEventDescriptor> Subscribe(long afterSequenceId)
    {
        lock (_stateGate)
        {
            return _feed.Subscribe(afterSequenceId, sequenceId =>
                CreateEvent(
                    sequenceId,
                    RuntimeEventKind.Snapshot,
                    _sessionGeneration,
                    _operationPhase,
                    _bootstrapState,
                    _activePackageIds));
        }
    }

    public RuntimeEventDescriptor PublishSessionGeneration(
        long generation,
        IReadOnlyList<string> activePackageIds,
        Action<RuntimeEventDescriptor>? committed = null)
    {
        var normalizedIds = NormalizePackageIds(activePackageIds);
        lock (_stateGate)
        {
            _sessionGeneration = generation;
            _activePackageIds = normalizedIds;
            var runtimeEvent = _feed.Publish(sequenceId => CreateEvent(
                sequenceId,
                RuntimeEventKind.SessionGenerationChanged,
                generation,
                _operationPhase,
                _bootstrapState,
                normalizedIds));
            committed?.Invoke(runtimeEvent);
            return runtimeEvent;
        }
    }

    public void PublishOperationPhase(RuntimeOperationPhase phase, IReadOnlyList<string>? packageIds = null)
    {
        lock (_stateGate)
        {
            _operationPhase = phase;
            _feed.Publish(sequenceId => CreateEvent(
                sequenceId,
                RuntimeEventKind.OperationPhaseChanged,
                _sessionGeneration,
                phase,
                _bootstrapState,
                NormalizePackageIds(packageIds ?? [])));
        }
    }

    public void PublishDevReloadResult(bool success, IReadOnlyList<string> packageIds, string? message)
    {
        lock (_stateGate)
        {
            _feed.Publish(sequenceId => CreateEvent(
                sequenceId,
                RuntimeEventKind.DevReloadCompleted,
                _sessionGeneration,
                _operationPhase,
                _bootstrapState,
                NormalizePackageIds(packageIds),
                success,
                NormalizeMessage(message)));
        }
    }

    public void PublishBootstrapState(
        RuntimeBootstrapState state,
        long generation,
        IReadOnlyList<string> activePackageIds,
        string? message,
        Action<RuntimeEventDescriptor>? committed = null)
    {
        var normalizedIds = NormalizePackageIds(activePackageIds);
        lock (_stateGate)
        {
            _bootstrapState = state;
            _sessionGeneration = generation;
            _activePackageIds = normalizedIds;
            var runtimeEvent = _feed.Publish(sequenceId => CreateEvent(
                sequenceId,
                RuntimeEventKind.BootstrapStateChanged,
                generation,
                _operationPhase,
                state,
                normalizedIds,
                state == RuntimeBootstrapState.Failed ? false : null,
                NormalizeMessage(message)));
            committed?.Invoke(runtimeEvent);
        }
    }

    public void PublishSnapshotDiagnostics(
        long generation,
        IReadOnlyList<string> activePackageIds,
        Action<RuntimeEventDescriptor> committed)
    {
        var normalizedIds = NormalizePackageIds(activePackageIds);
        lock (_stateGate)
        {
            var runtimeEvent = _feed.Publish(sequenceId => CreateEvent(
                sequenceId,
                RuntimeEventKind.SnapshotDiagnosticsChanged,
                generation,
                _operationPhase,
                _bootstrapState,
                normalizedIds));
            committed(runtimeEvent);
        }
    }

    public T Capture<T>(Func<long, T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_stateGate)
        {
            return capture(_feed.CurrentSequenceId);
        }
    }

    public void Complete()
    {
        lock (_stateGate)
        {
            _operationPhase = RuntimeOperationPhase.ShuttingDown;
            _feed.Complete();
        }
    }

    private RuntimeEventDescriptor CreateEvent(
        long sequenceId,
        RuntimeEventKind kind,
        long generation,
        RuntimeOperationPhase phase,
        RuntimeBootstrapState bootstrapState,
        IReadOnlyList<string> packageIds,
        bool? success = null,
        string? message = null)
        => new(
            _runtimeInstanceId,
            sequenceId,
            DateTimeOffset.UtcNow,
            kind,
            generation,
            phase,
            bootstrapState,
            packageIds,
            success,
            message);

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
