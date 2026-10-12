using System.Collections.Concurrent;

namespace Sunder.App.Tests.TestSupport;

/// <summary>Pumps awaited continuations on the control-creating test thread without owning Avalonia's global UI dispatcher.</summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();

    public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));

    public void RunUntilCompleted(Task task)
    {
        _ = task.ContinueWith(_ => _work.CompleteAdding(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var work in _work.GetConsumingEnumerable(deadline.Token)) work.Callback(work.State);
    }

    public void Dispose() => _work.Dispose();
}
