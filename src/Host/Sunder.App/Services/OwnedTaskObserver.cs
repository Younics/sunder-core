namespace Sunder.App.Services;

public sealed class OwnedTaskObserver : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _syncRoot = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly string _ownerName;
    private bool _stopped;
    private bool _disposed;
    private Task? _stopTask;

    public OwnedTaskObserver(string ownerName)
    {
        _ownerName = ownerName;
    }

    public CancellationToken Token => _lifetime.Token;

    public void Observe(Task task, string operation)
    {
        Task observedTask;
        lock (_syncRoot)
        {
            if (_stopped)
            {
                ObserveDetached(task, operation);
                return;
            }

            observedTask = ObserveCoreAsync(task, operation);
            _tasks.Add(observedTask);
        }

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

    public void Run(Func<CancellationToken, Task> operation, string operationName)
    {
        lock (_syncRoot)
        {
            if (_stopped)
            {
                return;
            }

            Observe(operation(_lifetime.Token), operationName);
        }
    }

    public Task StopAsync()
    {
        lock (_syncRoot)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _stopped = true;
            _lifetime.Cancel();
            _stopTask = Task.WhenAll(_tasks.ToArray());
            return _stopTask;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopped = true;
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    private async Task ObserveCoreAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"{_ownerName} failed while {operation}.", ex);
        }
    }

    private async void ObserveDetached(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"{_ownerName} failed while {operation}.", ex);
        }
    }
}
