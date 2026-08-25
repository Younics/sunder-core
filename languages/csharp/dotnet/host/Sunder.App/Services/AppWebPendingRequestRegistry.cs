using System.Diagnostics;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal sealed class AppWebPendingRequestRegistry
{
    internal const int MaximumRememberedRequestIds = 2048;

    internal enum ReservationOutcome
    {
        Reserved,
        RequestIdReused,
        CapacityReached,
    }

    private readonly object _authorityGate;
    private readonly Dictionary<string, AppWebPendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedOrder = new();

    // Storage deliberately requires the bridge gate so authentication and reservation stay atomic.
    public AppWebPendingRequestRegistry(object authorityGate)
    {
        ArgumentNullException.ThrowIfNull(authorityGate);
        _authorityGate = authorityGate;
    }

    public int Count
    {
        get
        {
            AssertAuthority();
            return _pending.Count;
        }
    }

    internal int RememberedRequestIdCount
    {
        get
        {
            AssertAuthority();
            return _rememberedIds.Count;
        }
    }

    public ReservationOutcome TryReserve(
        string requestId,
        Func<AppWebPendingRequest> requestFactory,
        out AppWebPendingRequest? pending)
    {
        AssertAuthority();
        pending = null;
        if (!_rememberedIds.Add(requestId))
        {
            return ReservationOutcome.RequestIdReused;
        }

        _rememberedOrder.Enqueue(requestId);
        while (_rememberedOrder.Count > MaximumRememberedRequestIds)
        {
            _rememberedIds.Remove(_rememberedOrder.Dequeue());
        }

        if (_pending.Count >= RuntimeClientResourceLimits.MaximumPendingBrowserRequests)
        {
            return ReservationOutcome.CapacityReached;
        }

        var reserved = requestFactory();
        _pending.Add(requestId, reserved);
        pending = reserved;
        return ReservationOutcome.Reserved;
    }

    public bool TryCancel(string requestId)
    {
        AssertAuthority();
        if (!_pending.TryGetValue(requestId, out var pending))
        {
            return false;
        }
        pending.Cancel();
        return true;
    }

    public bool IsCurrent(string requestId, AppWebPendingRequest pending)
    {
        AssertAuthority();
        return _pending.TryGetValue(requestId, out var current)
               && ReferenceEquals(current, pending)
               && !pending.IsCompleted;
    }

    public bool TryComplete(string requestId, AppWebPendingRequest pending)
    {
        AssertAuthority();
        var ownsRequest = _pending.TryGetValue(requestId, out var current)
                          && ReferenceEquals(current, pending);
        if (ownsRequest)
        {
            _pending.Remove(requestId);
        }
        var completed = pending.TryComplete();
        return ownsRequest && completed;
    }

    public AppWebPendingRequest[] DetachAll()
    {
        AssertAuthority();
        var requests = _pending.Values.ToArray();
        _pending.Clear();
        _rememberedIds.Clear();
        _rememberedOrder.Clear();
        return requests;
    }

    // Detached request signals stay outside the bridge gate and are atomic/idempotent.
    public static void Cancel(IEnumerable<AppWebPendingRequest> requests)
    {
        foreach (var request in requests)
        {
            request.Cancel();
        }
    }

    public static void CancelAndDispose(IEnumerable<AppWebPendingRequest> requests)
    {
        foreach (var request in requests)
        {
            request.Cancel();
            request.Dispose();
        }
    }

    [Conditional("DEBUG")]
    private void AssertAuthority()
        => Debug.Assert(
            Monitor.IsEntered(_authorityGate),
            "Pending browser request storage must be accessed under the AppWebBridge authority gate.");
}
