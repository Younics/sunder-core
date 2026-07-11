namespace Sunder.App.Services;

public sealed class OwnedTaskObserver : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _ownerName;
    private bool _disposed;

    public OwnedTaskObserver(string ownerName)
    {
        _ownerName = ownerName;
    }

    public CancellationToken Token => _lifetime.Token;

    public void Observe(Task task, string operation)
    {
        if (_disposed)
        {
            return;
        }

        _ = ObserveCoreAsync(task, operation);
    }

    public void Run(Func<CancellationToken, Task> operation, string operationName)
    {
        if (_disposed)
        {
            return;
        }

        Observe(operation(_lifetime.Token), operationName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
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
}
