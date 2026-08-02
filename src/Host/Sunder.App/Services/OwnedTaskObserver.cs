namespace Sunder.App.Services;

public sealed class OwnedTaskObserver : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _syncRoot = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly string _ownerName;
    private readonly CancellationToken _lifetimeToken;
    private bool _stopped;
    private bool _disposed;
    private Task? _stopTask;

    public OwnedTaskObserver(string ownerName)
    {
        _ownerName = ownerName;
        _lifetimeToken = _lifetime.Token;
    }

    public CancellationToken Token => _lifetimeToken;

    public void Observe(Task task, string operation)
    {
        Task? observedTask = null;
        var observeDetached = false;
        lock (_syncRoot)
        {
            if (_stopped)
            {
                observeDetached = true;
            }
            else
            {
                observedTask = ObserveCoreAsync(task, operation, _lifetimeToken);
                _tasks.Add(observedTask);
            }
        }

        if (observeDetached)
        {
            ObserveDetached(task, operation, _lifetimeToken);
            return;
        }

        TrackCompletion(observedTask!);
    }

    public void Run(Func<CancellationToken, Task> operation, string operationName)
        => _ = RunTracked(operation, operationName);

    internal Task RunTracked(Func<CancellationToken, Task> operation, string operationName)
    {
        Task observedTask;
        lock (_syncRoot)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            observedTask = ObserveOperationAsync(operation, operationName, _lifetimeToken);
            _tasks.Add(observedTask);
        }

        TrackCompletion(observedTask);
        return observedTask;
    }

    private void TrackCompletion(Task observedTask)
    {
        _ = observedTask.ContinueWith(
            completed =>
            {
                lock (_syncRoot)
                {
                    _tasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task StopAsync()
    {
        Task stopTask;
        lock (_syncRoot)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _stopped = true;
            stopTask = Task.WhenAll(_tasks.ToArray());
            _stopTask = stopTask;
        }

        TryCancelLifetime();
        return stopTask;
    }

    public void Dispose()
    {
        var shouldDispose = false;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopped = true;
            shouldDispose = true;
        }

        if (shouldDispose)
        {
            TryCancelLifetime();
            _lifetime.Dispose();
        }
    }

    private async Task ObserveCoreAsync(
        Task task,
        string operation,
        CancellationToken lifetimeToken)
    {
        await Task.Yield();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"{_ownerName} failed while {operation}.", ex);
        }
    }

    private async Task ObserveOperationAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken lifetimeToken)
    {
        await Task.Yield();
        try
        {
            await operation(lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"{_ownerName} failed while {operationName}.", ex);
        }
    }

    private async void ObserveDetached(
        Task task,
        string operation,
        CancellationToken lifetimeToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"{_ownerName} failed while {operation}.", ex);
        }
    }

    private void TryCancelLifetime()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent dispose already delivered cancellation.
        }
    }
}
