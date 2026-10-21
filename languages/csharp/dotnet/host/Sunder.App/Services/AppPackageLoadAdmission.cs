namespace Sunder.App.Services;

// Shell-owned policy authority. Package services never receive permit identifiers
// or the process-control connection. Local disposal and Host retirement are separate.
internal sealed class AppPackageLoadAdmission(Guid activationId, string? permitId,
    Func<CancellationToken, Task>? retire = null)
{
    private static readonly AsyncLocal<CleanupNode?> Cleanup = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _retired;
    internal Guid ActivationId { get; } = activationId;
    internal string? PermitId { get; } = permitId;
    internal string? PolicySessionId { get; init; }
    internal static bool IsCleaningActivation
    {
        get
        {
            for (var node = Cleanup.Value; node is not null; node = node.Parent)
                if (node.Active) return true;
            return false;
        }
    }

    internal static void RequireOutsideCleanup(Guid activationId)
    {
        for (var node = Cleanup.Value; node is not null; node = node.Parent)
            if (node.Active && node.ActivationId == activationId)
                throw new InvalidOperationException("Development-owner cleanup has an activation dependency cycle.");
    }

    internal IDisposable EnterCleanup()
    {
        RequireOutsideCleanup(ActivationId);
        var node = new CleanupNode(ActivationId, Cleanup.Value);
        Cleanup.Value = node;
        return node;
    }

    private sealed class CleanupNode(Guid activationId, CleanupNode? parent) : IDisposable
    {
        private int _active = 1;
        internal bool Active => Volatile.Read(ref _active) == 1;
        internal Guid ActivationId { get; } = activationId;
        internal CleanupNode? Parent { get; } = parent;
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            if (ReferenceEquals(Cleanup.Value, this)) Cleanup.Value = Parent;
        }
    }

    internal async Task RetireAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_retired) return;
            if (retire is not null) await retire(token).ConfigureAwait(false);
            _retired = true;
        }
        finally { _gate.Release(); }
    }
}
