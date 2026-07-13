using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using PackageCallbackCancellationReason = Sunder.Sdk.Callbacks.PackageCallbackCancellationReason;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageCallbackSessionStore
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ActivePackageCallbackSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<PackageCallbackStartKey, ActivePackageCallbackSession> _starts = [];
    private bool _stopping;

    public PackageCallbackStartReservation BeginStart(
        PackageCallbackStartKey key,
        int maximumSessions,
        Func<ActivePackageCallbackSession> createSession)
    {
        lock (_syncRoot)
        {
            if (_stopping)
            {
                throw new RuntimeUnavailableException("Package callback sessions are shutting down.");
            }
            if (_starts.TryGetValue(key, out var existing))
            {
                return new(existing, IsOwner: false);
            }
            if (_sessions.Count >= maximumSessions)
            {
                throw new RuntimeConflictException("Too many package callback sessions are active. Wait for an existing session to finish and retry.");
            }

            var session = createSession();
            _sessions.Add(session.SessionId, session);
            _starts.Add(key, session);
            return new(session, IsOwner: true);
        }
    }

    public bool TryPublishPending(ActivePackageCallbackSession session, PackageCallbackSessionResponse status)
    {
        lock (_syncRoot)
        {
            if (_stopping
                || !_sessions.TryGetValue(session.SessionId, out var current)
                || !ReferenceEquals(current, session)
                || session.State != PackageCallbackLifecycleState.Starting)
            {
                return false;
            }

            session.CallbackServer.RegisterHandler(session.SessionId, session.CompleteAsync);
            session.Status = status;
            session.State = PackageCallbackLifecycleState.Pending;
            return true;
        }
    }

    public bool TryFinishStart(ActivePackageCallbackSession session, PackageCallbackLifecycleState terminalState)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(session.SessionId, out var current)
                || !ReferenceEquals(current, session)
                || session.State != PackageCallbackLifecycleState.Starting)
            {
                return false;
            }

            session.State = terminalState;
            RemoveLocked(session);
            return true;
        }
    }

    public PackageCallbackSessionResponse? GetStatus(string packageId, string sessionId, long generation)
    {
        lock (_syncRoot)
        {
            return _sessions.TryGetValue(sessionId, out var session)
                   && session.Generation == generation
                   && string.Equals(session.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                ? session.Status
                : null;
        }
    }

    public ActivePackageCallbackSession? TryClaimCompletion(string sessionId, long generation)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)
                || session.Generation != generation
                || session.State != PackageCallbackLifecycleState.Pending)
            {
                return null;
            }

            session.State = PackageCallbackLifecycleState.Completing;
            session.WorkCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.CallbackServer.UnregisterHandler(sessionId);
            return session;
        }
    }

    public void Complete(
        ActivePackageCallbackSession session,
        PackageCallbackSessionResponse status,
        DateTimeOffset removeAtUtc)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(session.SessionId, out var current)
                || !ReferenceEquals(current, session)
                || session.State != PackageCallbackLifecycleState.Completing)
            {
                return;
            }

            session.Status = status;
            session.ExpiresAtUtc = removeAtUtc;
            RemoveStartLocked(session);
            session.State = status.State switch
            {
                PackageCallbackSessionState.Completed => PackageCallbackLifecycleState.Completed,
                PackageCallbackSessionState.Cancelled => PackageCallbackLifecycleState.Cancelled,
                PackageCallbackSessionState.Expired => PackageCallbackLifecycleState.Expired,
                _ => PackageCallbackLifecycleState.Failed,
            };
        }
    }

    public IReadOnlyList<PackageCallbackCancellationWork> Sweep(DateTimeOffset now, TimeSpan terminalRetention)
    {
        lock (_syncRoot)
        {
            var cancellations = new List<PackageCallbackCancellationWork>();
            foreach (var session in _sessions.Values.ToArray())
            {
                if (session.ExpiresAtUtc > now || session.State == PackageCallbackLifecycleState.Completing)
                {
                    continue;
                }

                if (session.State == PackageCallbackLifecycleState.Starting)
                {
                    session.State = PackageCallbackLifecycleState.Expired;
                    RemoveLocked(session);
                    cancellations.Add(new(session, PackageCallbackCancellationReason.Expired));
                }
                else if (session.State == PackageCallbackLifecycleState.Pending)
                {
                    session.CallbackServer.UnregisterHandler(session.SessionId);
                    session.State = PackageCallbackLifecycleState.Expired;
                    session.ExpiresAtUtc = now + terminalRetention;
                    session.Status = session.Status! with
                    {
                        State = PackageCallbackSessionState.Expired,
                        Message = "The callback session expired.",
                    };
                    RemoveStartLocked(session);
                    cancellations.Add(new(session, PackageCallbackCancellationReason.Expired));
                }
                else
                {
                    RemoveLocked(session);
                }
            }
            return cancellations;
        }
    }

    public IReadOnlyList<PackageCallbackCancellationWork> RemovePackage(string packageId)
        => CancelWhere(
            session => string.Equals(session.PackageId, packageId, StringComparison.OrdinalIgnoreCase),
            PackageCallbackCancellationReason.PackageUnloaded,
            beginShutdown: false);

    public IReadOnlyList<PackageCallbackCancellationWork> Clear()
        => CancelWhere(
            static _ => true,
            PackageCallbackCancellationReason.PackageUnloaded,
            beginShutdown: false);

    public PackageCallbackShutdownWork BeginShutdown()
    {
        var cancellations = CancelWhere(
            static _ => true,
            PackageCallbackCancellationReason.HostShutdown,
            beginShutdown: true);
        lock (_syncRoot)
        {
            return new PackageCallbackShutdownWork(
                cancellations,
                _sessions.Values
                    .Where(static session => session.State == PackageCallbackLifecycleState.Completing)
                    .Select(static session => session.WorkCompletion.Task)
                    .ToArray());
        }
    }

    private IReadOnlyList<PackageCallbackCancellationWork> CancelWhere(
        Func<ActivePackageCallbackSession, bool> predicate,
        PackageCallbackCancellationReason reason,
        bool beginShutdown)
    {
        lock (_syncRoot)
        {
            if (beginShutdown)
            {
                _stopping = true;
            }

            var cancellations = new List<PackageCallbackCancellationWork>();
            foreach (var session in _sessions.Values.Where(predicate).ToArray())
            {
                if (session.State == PackageCallbackLifecycleState.Completing)
                {
                    continue;
                }
                if (session.State is PackageCallbackLifecycleState.Starting or PackageCallbackLifecycleState.Pending)
                {
                    session.CallbackServer.UnregisterHandler(session.SessionId);
                    session.State = PackageCallbackLifecycleState.Cancelled;
                    RemoveLocked(session);
                    cancellations.Add(new(session, reason));
                }
                else
                {
                    RemoveLocked(session);
                }
            }
            return cancellations;
        }
    }

    private void RemoveLocked(ActivePackageCallbackSession session)
    {
        _sessions.Remove(session.SessionId);
        RemoveStartLocked(session);
    }

    private void RemoveStartLocked(ActivePackageCallbackSession session)
    {
        if (_starts.TryGetValue(session.StartKey, out var current) && ReferenceEquals(current, session))
        {
            _starts.Remove(session.StartKey);
        }
    }
}

internal enum PackageCallbackLifecycleState
{
    Starting,
    Pending,
    Completing,
    Completed,
    Failed,
    Cancelled,
    Expired,
}

internal readonly record struct PackageCallbackStartKey(
    string PackageId,
    long Generation,
    string CallbackHandlerId,
    string ParametersKey);

internal readonly record struct PackageCallbackStartReservation(
    ActivePackageCallbackSession Session,
    bool IsOwner);

internal readonly record struct PackageCallbackCancellationWork(
    ActivePackageCallbackSession Session,
    PackageCallbackCancellationReason Reason);

internal readonly record struct PackageCallbackShutdownWork(
    IReadOnlyList<PackageCallbackCancellationWork> Cancellations,
    IReadOnlyList<Task> CompletingTasks);

internal sealed class ActivePackageCallbackSession(
    string packageId,
    long generation,
    PackageCallbackStartKey startKey,
    string sessionId,
    IPackageCallbackHandler handler,
    PackageCallbackServer callbackServer,
    DateTimeOffset expiresAtUtc,
    CancellationTokenSource pendingCancellation,
    Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>> completeAsync)
{
    public string PackageId { get; } = packageId;
    public long Generation { get; } = generation;
    public PackageCallbackStartKey StartKey { get; } = startKey;
    public string SessionId { get; } = sessionId;
    public IPackageCallbackHandler Handler { get; } = handler;
    public PackageCallbackServer CallbackServer { get; } = callbackServer;
    public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
    public CancellationTokenSource PendingCancellation { get; } = pendingCancellation;
    public Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<bool>> CompleteAsync { get; } = completeAsync;
    public PackageCallbackLifecycleState State { get; set; } = PackageCallbackLifecycleState.Starting;
    public PackageCallbackSessionResponse? Status { get; set; }
    public TaskCompletionSource<PackageCallbackSessionResponse?> StartCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WorkCompletion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
