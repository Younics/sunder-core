namespace Sunder.App.Services;

internal sealed class AppPackageGenerationPublication
{
    private readonly object _syncRoot = new();
    private readonly List<Action> _bufferedActions = [];
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
            _state = PublicationState.Revoked;
            _bufferedActions.Clear();
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
}
