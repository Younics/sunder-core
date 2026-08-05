namespace Sunder.App.Services;

internal sealed class AppPackageGenerationRetirementQueue : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly Func<AppPackageGeneration, ValueTask> _retireGenerationAsync;
    private readonly TimeSpan _retirementBudget;
    private readonly TimeSpan _drainBudget;
    private readonly List<Exception> _failures = [];
    private readonly List<AppPackageGeneration> _quarantinedGenerations = [];
    private Task _tail = Task.CompletedTask;
    private int _pendingCount;
    private int _quarantinedCount;
    private bool _disposed;

    public AppPackageGenerationRetirementQueue(
        Func<AppPackageGeneration, ValueTask>? retireGenerationAsync = null,
        TimeSpan? retirementBudget = null,
        TimeSpan? drainBudget = null)
    {
        _retireGenerationAsync = retireGenerationAsync ?? (generation => generation.DisposeAsync());
        _retirementBudget = retirementBudget ?? AppShutdownBudgets.GenerationRetirement;
        _drainBudget = drainBudget ?? AppShutdownBudgets.RetirementDrain;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_retirementBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_drainBudget, TimeSpan.Zero);
    }

    internal int PendingCount => Volatile.Read(ref _pendingCount);

    internal int QuarantinedCount => Volatile.Read(ref _quarantinedCount);

    internal int FailureCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _failures.Count;
            }
        }
    }

    public void Enqueue(AppPackageGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _pendingCount);
            var predecessor = _tail;
            _tail = Task.Run(() => RetireAfterAsync(predecessor, generation));
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task tail;
        lock (_syncRoot)
        {
            tail = _tail;
        }
        return tail.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task tail;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            tail = _tail;
        }

        try
        {
            await tail.WaitAsync(_drainBudget).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            RecordFailure(ex);
            Interlocked.Increment(ref _quarantinedCount);
            AppSessionLog.WriteError(
                $"App package retirement queue did not drain within {_drainBudget.TotalSeconds:0.###} seconds; remaining generations were quarantined.",
                ex);
            AppCleanupQuarantine.Retain(tail, "draining the App package generation retirement queue");
        }
    }

    private async Task RetireAfterAsync(Task predecessor, AppPackageGeneration generation)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
            Task retirementTask;
            try
            {
                retirementTask = _retireGenerationAsync(generation).AsTask();
            }
            catch (Exception ex)
            {
                RecordRetirementFailure(generation, ex);
                return;
            }

            try
            {
                await retirementTask.WaitAsync(_retirementBudget).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Quarantine(generation, ex);
                AppSessionLog.WriteError(
                    $"App package generation '{generation.Id}' cleanup exceeded its {_retirementBudget.TotalSeconds:0.###} second budget and was quarantined without forcing ALC unload.",
                    ex);
                AppCleanupQuarantine.Retain(retirementTask, $"retiring App package generation '{generation.Id}'");
            }
            catch (Exception ex)
            {
                RecordRetirementFailure(generation, ex);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    private void RecordRetirementFailure(AppPackageGeneration generation, Exception exception)
    {
        Quarantine(generation, exception);
        AppSessionLog.WriteError(
            $"App package generation '{generation.Id}' was replaced but could not be fully retired.",
            exception);
    }

    private void RecordFailure(Exception exception)
    {
        lock (_syncRoot)
        {
            _failures.Add(exception);
        }
    }

    private void Quarantine(AppPackageGeneration generation, Exception exception)
    {
        lock (_syncRoot)
        {
            _failures.Add(exception);
            _quarantinedGenerations.Add(generation);
        }
        Interlocked.Increment(ref _quarantinedCount);
    }
}
