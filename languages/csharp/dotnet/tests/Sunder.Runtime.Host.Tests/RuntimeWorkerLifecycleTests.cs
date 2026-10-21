using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeWorkerLifecycleTests
{
    [Fact]
    public async Task BatchStopAdmitsEveryIndependentWorkerBeforeWaitingOnTheSharedDeadline()
    {
        const int count = 15;
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new RecordingLogSink();
        var workers = Enumerable.Range(0, count).Select(index => new StopWorker(async token =>
        {
            if (Interlocked.Increment(ref entered) == count) allEntered.TrySetResult();
            await allEntered.Task.WaitAsync(token);
        })).ToArray();
        var operations = new List<Task>();
        try
        {
            await RuntimeWorkerLifecycle.StopWorkersAsync(
                workers.Select((worker, index) => ("package." + index, (IRuntimePackageWorker)worker)).ToArray(),
                logs.CreateLogger<RuntimeWorkerLifecycleTests>(), TimeSpan.FromSeconds(3), operationStarted: operations.Add);
            Assert.Equal(count, entered);
            Assert.All(operations, operation => Assert.True(operation.IsCompletedSuccessfully));
            Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        }
        finally { allEntered.TrySetResult(); }
    }

    [Fact]
    public async Task DeadlineLeavesExactUnfinishedStopOwnedWithoutBlockingHealthyPeerOrInventingDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new StopWorker(async _ => { entered.TrySetResult(); await release.Task; });
        var healthy = new StopWorker(_ => Task.CompletedTask);
        var operations = new List<Task>();
        var logs = new RecordingLogSink();
        try
        {
            var stop = RuntimeWorkerLifecycle.StopWorkersAsync(
                [("blocked", blocked), ("healthy", healthy)], logs.CreateLogger<RuntimeWorkerLifecycleTests>(),
                TimeSpan.FromMilliseconds(100), operationStarted: operations.Add);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, operations.Count);
            Assert.False(operations[0].IsCompleted);
            Assert.True(operations[1].IsCompletedSuccessfully);
            Assert.False(blocked.Disposed);
            Assert.False(healthy.Disposed);
            var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal("blocked", warning.Properties["PackageId"]);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task PreCancelledStopDoesNotAdmitCleanupWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        var worker = new StopWorker(_ => { called = true; return Task.CompletedTask; });
        var operations = new List<Task>();
        await RuntimeWorkerLifecycle.StopWorkersAsync([("cancelled", worker)],
            new RecordingLogSink().CreateLogger<RuntimeWorkerLifecycleTests>(), TimeSpan.FromSeconds(1),
            cancellation.Token, operations.Add);
        Assert.False(called);
        Assert.Empty(operations);
    }

    private sealed class StopWorker(Func<CancellationToken, Task> stop) : TestRuntimePackageWorker
    {
        internal bool Disposed { get; private set; }
        public override Task StopAsync(CancellationToken cancellationToken = default) => stop(cancellationToken);
        public override ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
