namespace Sunder.App.Services;

internal sealed class AppPackageGenerationPublication
{
    private readonly object _syncRoot = new();
    private readonly List<Action> _bufferedActions = [];
    private readonly AsyncLocal<RuntimePreparationAuthority?> _runtimePreparation = new();
    private readonly CancellationTokenSource _revocation = new();
    private PublicationState _state;

    public bool IsPublished
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Published;
            }
        }
    }

    public bool IsRevoked
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Revoked;
            }
        }
    }

    public bool IsRuntimeAvailable
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Published
                       || _state == PublicationState.Pending
                       && _runtimePreparation.Value is { IsActive: true };
            }
        }
    }

    public IDisposable BeginRuntimePreparation()
    {
        lock (_syncRoot)
        {
            if (_state != PublicationState.Pending)
            {
                throw new InvalidOperationException(
                    "Runtime preparation authority is only available to a pending App generation.");
            }
        }

        var previous = _runtimePreparation.Value;
        var authority = new RuntimePreparationAuthority();
        _runtimePreparation.Value = authority;
        return new RuntimePreparationScope(this, authority, previous);
    }

    public void Buffer(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_syncRoot)
        {
            if (_state == PublicationState.Pending)
            {
                _bufferedActions.Add(publish);
                return;
            }

            if (_state == PublicationState.Revoked)
            {
                return;
            }
        }

        publish();
    }

    public void Publish()
    {
        Action[] actions;
        lock (_syncRoot)
        {
            if (_state != PublicationState.Pending)
            {
                return;
            }

            _state = PublicationState.Published;
            actions = _bufferedActions.ToArray();
            _bufferedActions.Clear();
        }

        foreach (var action in actions)
        {
            InvokeBufferedAction(action);
        }
    }

    public void Revoke()
    {
        lock (_syncRoot)
        {
            if (_state == PublicationState.Revoked)
            {
                return;
            }

            _state = PublicationState.Revoked;
            _bufferedActions.Clear();
        }

        try
        {
            _revocation.Cancel();
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "Failed to cancel work owned by a retired App package generation.",
                exception);
        }
    }

    public void RequirePublished(string capability)
    {
        if (!IsPublished)
        {
            throw new InvalidOperationException(
                $"Package capability '{capability}' is unavailable until its App generation has been published.");
        }
    }

    public CancellationToken RequireRuntimeAccess(string capability)
    {
        lock (_syncRoot)
        {
            if (_state == PublicationState.Published
                || _state == PublicationState.Pending
                && _runtimePreparation.Value is { IsActive: true })
            {
                return _revocation.Token;
            }
        }

        throw new InvalidOperationException(
            $"Package capability '{capability}' is unavailable outside its current App generation or candidate preparation.");
    }

    private void EndRuntimePreparation(
        RuntimePreparationAuthority authority,
        RuntimePreparationAuthority? previous)
    {
        authority.Revoke();
        if (ReferenceEquals(_runtimePreparation.Value, authority))
        {
            _runtimePreparation.Value = previous;
        }
    }

    private static void InvokeBufferedAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to publish a buffered App package side effect.", exception);
        }
    }

    private enum PublicationState
    {
        Pending,
        Published,
        Revoked,
    }

    private sealed class RuntimePreparationAuthority
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Revoke() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class RuntimePreparationScope(
        AppPackageGenerationPublication owner,
        RuntimePreparationAuthority authority,
        RuntimePreparationAuthority? previous) : IDisposable
    {
        private AppPackageGenerationPublication? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndRuntimePreparation(authority, previous);
    }
}
