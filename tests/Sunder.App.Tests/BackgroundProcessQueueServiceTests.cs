using System.Collections.Concurrent;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class BackgroundProcessQueueServiceTests
{
    [Fact]
    public async Task Enqueue_WithSequentialGroup_BlocksSameGroupButRunsOtherGroups()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 2);
        var started = new ConcurrentQueue<string>();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(CreateRequest("a", "package-store", async _ =>
        {
            started.Enqueue("a");
            await releaseA.Task;
        }));
        queue.Enqueue(CreateRequest("b", "package-store", async _ =>
        {
            started.Enqueue("b");
            await releaseB.Task;
        }));
        queue.Enqueue(CreateRequest("c", "thumbnail-cache", async _ =>
        {
            started.Enqueue("c");
            await releaseC.Task;
        }));

        await WaitForConditionAsync(() => started.Contains("a") && started.Contains("c"));
        Assert.DoesNotContain("b", started);

        releaseA.SetResult();
        await WaitForConditionAsync(() => started.Contains("b"));

        releaseB.SetResult();
        releaseC.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task ReportProgress_UpdatesSnapshotStatusAndProgress()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var snapshots = new ConcurrentQueue<BackgroundProcessSnapshot>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.ProcessChanged += (_, e) => snapshots.Enqueue(e.Snapshot);

        queue.Enqueue(CreateRequest("progress", "work", async context =>
        {
            context.ReportProgress(42, "Halfway there");
            await release.Task;
        }));

        await WaitForConditionAsync(() => snapshots.Any(snapshot => snapshot.ProgressPercent == 42 && snapshot.StatusText == "Halfway there"));
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public void ProcessChanged_WhenSubscriberThrows_ContinuesNotifyingRemainingSubscribers()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var receivedQueuedSnapshot = false;

        queue.ProcessChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        queue.ProcessChanged += (_, e) =>
        {
            if (e.Snapshot.State == BackgroundProcessState.Queued)
            {
                receivedQueuedSnapshot = true;
            }
        };

        queue.Enqueue(CreateRequest("event", "work", _ => Task.CompletedTask));

        Assert.True(receivedQueuedSnapshot);
    }

    [Fact]
    public async Task Cancel_WhenProcessIsQueued_MarksCancelledWithoutStartingIt()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var started = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(CreateRequest("a", "work", async _ =>
        {
            started.Enqueue("a");
            await release.Task;
        }));
        var queued = queue.Enqueue(CreateRequest("b", "work", _ =>
        {
            started.Enqueue("b");
            return Task.CompletedTask;
        }));

        await WaitForConditionAsync(() => started.Contains("a"));
        Assert.True(queue.Cancel(queued.ProcessId));

        var cancelled = queue.GetProcess(queued.ProcessId);
        Assert.Equal(BackgroundProcessState.Cancelled, cancelled?.State);
        Assert.DoesNotContain("b", started);

        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task CancelAllAsync_CancelsRunningAndQueuedProcessesRegardlessOfCanCancel()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        queue.Enqueue(CreateRequest("a", "work", async context =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
        }, canCancel: false));
        var queued = queue.Enqueue(CreateRequest("b", "work", _ => Task.CompletedTask, canCancel: false));

        await WaitForConditionAsync(() => queue.ListProcesses().Any(process => process.State == BackgroundProcessState.Running));
        await queue.CancelAllAsync();

        Assert.All(queue.ListProcesses(), process => Assert.Equal(BackgroundProcessState.Cancelled, process.State));
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(queued.ProcessId)?.State);
    }

    [Fact]
    public async Task DisposeAsync_CancelsQueuedAndRunningProcessesAndWaitsForCleanup()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedStarted = false;

        var running = queue.Enqueue(CreateRequest("running", "work", async context =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                cancellationObserved.SetResult();
                await allowCleanup.Task;
            }
        }, canCancel: false));
        var queued = queue.Enqueue(CreateRequest("queued", "work", _ =>
        {
            queuedStarted = true;
            return Task.CompletedTask;
        }, canCancel: false));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = queue.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(BackgroundProcessState.Cancelling, queue.GetProcess(running.ProcessId)?.State);
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(queued.ProcessId)?.State);
        Assert.False(queuedStarted);

        allowCleanup.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(queue.ListProcesses(), process => Assert.Equal(BackgroundProcessState.Cancelled, process.State));
    }

    [Fact]
    public async Task Dispose_CancelsRunningProcessesWithoutWaitingForCleanup()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = queue.Enqueue(CreateRequest("running", "work", async context =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                cancellationObserved.SetResult();
                await allowCleanup.Task;
            }
        }, canCancel: false));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Dispose();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BackgroundProcessState.Cancelling, queue.GetProcess(running.ProcessId)?.State);

        allowCleanup.SetResult();
        await WaitForConditionAsync(() => queue.GetProcess(running.ProcessId)?.State == BackgroundProcessState.Cancelled);
    }

    [Fact]
    public void Enqueue_AfterDispose_ThrowsObjectDisposedException()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        queue.Dispose();

        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(CreateRequest("after dispose", "work", _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task Cancellation_AfterDispose_IsSafeNoOp()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        queue.Dispose();

        Assert.False(queue.Cancel(Guid.NewGuid()));
        await queue.CancelAllAsync();
    }

    [Fact]
    public async Task Cancel_WhenRunningProcessReturnsAfterCancellation_MarksCancelled()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = queue.Enqueue(CreateRequest("cancel", "work", async _ =>
        {
            started.SetResult();
            await release.Task;
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(queue.Cancel(process.ProcessId));
        release.SetResult();

        await WaitForConditionAsync(() => queue.GetProcess(process.ProcessId)?.IsTerminal == true);
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(process.ProcessId)?.State);
    }

    [Fact]
    public void Enqueue_WhenHiddenIndicatorIsSet_ExposesHiddenSnapshot()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);

        var process = queue.Enqueue(CreateRequest("hidden", "work", _ => Task.CompletedTask, indicator: BackgroundProcessIndicator.Hidden));

        Assert.Equal(BackgroundProcessIndicator.Hidden, process.Indicator);
        Assert.Equal(BackgroundProcessIndicator.Hidden, queue.GetProcess(process.ProcessId)?.Indicator);
    }

    [Fact]
    public async Task CompletedHistory_TrimsToConfiguredMaximum()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1, maxCompletedHistory: 2);

        queue.Enqueue(CreateRequest("a", "work-a", _ => Task.CompletedTask));
        queue.Enqueue(CreateRequest("b", "work-b", _ => Task.CompletedTask));
        queue.Enqueue(CreateRequest("c", "work-c", _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.ListProcesses().Count == 2 && queue.ListProcesses().All(process => process.IsTerminal));

        var titles = queue.ListProcesses().Select(process => process.Title).ToArray();
        Assert.DoesNotContain("a", titles);
        Assert.Contains("b", titles);
        Assert.Contains("c", titles);
    }

    [Fact]
    public async Task CompletedHistory_WhenMaximumIsZero_RemovesTerminalProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1, maxCompletedHistory: 0);

        queue.Enqueue(CreateRequest("removed", "work", _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.ListProcesses().Count == 0);
    }

    [Fact]
    public async Task Enqueue_WhenProcessFails_PublishesFailedSnapshotWithErrorMessage()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var snapshots = new ConcurrentQueue<BackgroundProcessSnapshot>();
        queue.ProcessChanged += (_, e) => snapshots.Enqueue(e.Snapshot);

        var process = queue.Enqueue(CreateRequest("failure", "work", _ => throw new InvalidOperationException("boom")));

        await WaitForConditionAsync(() => queue.GetProcess(process.ProcessId)?.State == BackgroundProcessState.Failed);

        var failed = queue.GetProcess(process.ProcessId);
        Assert.Equal("boom", failed?.ErrorMessage);
        Assert.Contains(snapshots, snapshot => snapshot.ProcessId == process.ProcessId
                                              && snapshot.State == BackgroundProcessState.Failed
                                              && snapshot.ErrorMessage == "boom");
    }

    [Fact]
    public async Task BackgroundProcessMonitor_WithMainIndicatorFilter_ExcludesHiddenProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(CreateRequest("hidden", "hidden-work", async _ => await release.Task, indicator: BackgroundProcessIndicator.Hidden));
        queue.Enqueue(CreateRequest("visible", "visible-work", async _ => await release.Task, indicator: BackgroundProcessIndicator.Main));

        await WaitForConditionAsync(() => queue.ListProcesses().Count(process => process.State == BackgroundProcessState.Running) == 2);
        using var monitor = new BackgroundProcessMonitorViewModel(queue, BackgroundProcessIndicator.Main);

        Assert.Collection(monitor.Processes, process => Assert.Equal("visible", process.Title));

        monitor.Dispose();
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task BackgroundProcessMonitor_WithPackageIndicatorFilter_IncludesOnlyPackageProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(CreateRequest("package", PackageOperationService.PackageStoreGroupKey, async _ => await release.Task, indicator: BackgroundProcessIndicator.Packages));
        queue.Enqueue(CreateRequest("visible", "other", async _ => await release.Task, indicator: BackgroundProcessIndicator.Main));

        await WaitForConditionAsync(() => queue.ListProcesses().Count(process => process.State == BackgroundProcessState.Running) == 2);
        using var monitor = new BackgroundProcessMonitorViewModel(
            queue,
            BackgroundProcessIndicator.Packages);

        Assert.Collection(monitor.Processes, process => Assert.Equal("package", process.Title));

        monitor.Dispose();
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task PackageScopedBackgroundProcessQueue_MapsPackageProcessesAndPublishesChanges()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        using var packageQueue = new PackageScopedBackgroundProcessQueue("test.package", "Test Package", queue);
        packageQueue.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<BackgroundProcessSnapshot>();
        packageQueue.ProcessChanged += (_, e) => changes.Enqueue(e.Snapshot);

        var snapshot = packageQueue.Enqueue(new BackgroundProcessRequest(
            "Pull image",
            "docker-image-pulls",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            async context =>
            {
                context.ReportIndeterminate("Pulling layer");
                await release.Task;
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["imageReference"] = "agent0ai/agent-zero:latest",
            }));

        Assert.Equal(BackgroundProcessIndicator.Settings, snapshot.Indicator);
        Assert.Equal("docker-image-pulls", snapshot.GroupKey);
        Assert.Equal("agent0ai/agent-zero:latest", snapshot.Metadata["imageReference"]);
        await WaitForConditionAsync(() => changes.Any(change => change.StatusText == "Pulling layer"));

        var appSnapshot = Assert.Single(queue.ListProcesses());
        Assert.Equal("package:test.package:docker-image-pulls", appSnapshot.GroupKey);
        Assert.True(PackageScopedBackgroundProcessMetadata.TryCreate(appSnapshot.Metadata, out var packageMetadata));
        Assert.Equal("Test Package", packageMetadata.PackageDisplayName);

        var listed = Assert.Single(packageQueue.ListProcesses("docker-image-pulls"));
        Assert.Equal(snapshot.ProcessId, listed.ProcessId);

        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task PackageScopedBackgroundProcessQueue_Start_WhenCalledTwice_DoesNotDuplicateChangeEvents()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        using var packageQueue = new PackageScopedBackgroundProcessQueue("test.package", "Test Package", queue);
        packageQueue.Start();
        packageQueue.Start();
        var queuedChangeCount = 0;
        packageQueue.ProcessChanged += (_, e) =>
        {
            if (e.Snapshot.State == BackgroundProcessState.Queued)
            {
                queuedChangeCount++;
            }
        };

        var snapshot = packageQueue.Enqueue(new BackgroundProcessRequest(
            "Package work",
            "work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask));

        Assert.Equal(1, queuedChangeCount);
        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
    }

    [Fact]
    public async Task PackageScopedBackgroundProcessQueue_StopAsync_CancelsAndAwaitsRunningProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundProcessQueue("test.package", "Test Package", queue);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var snapshot = packageQueue.Enqueue(new BackgroundProcessRequest(
            "Long package work",
            "work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: false,
            async context =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    await allowCleanup.Task;
                }
            }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopTask = packageQueue.StopAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(stopTask.IsCompleted);

        allowCleanup.SetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BackgroundProcessState.Cancelled, queue.GetProcess(snapshot.ProcessId)?.State);
    }

    [Fact]
    public async Task PackageScopedBackgroundProcessQueue_AfterStop_RejectsNewWorkAndHidesPackageProcesses()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundProcessQueue("test.package", "Test Package", queue);
        var snapshot = packageQueue.Enqueue(new BackgroundProcessRequest(
            "Package work",
            "work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.GetProcess(snapshot.ProcessId)?.IsTerminal == true);
        await packageQueue.StopAsync();

        Assert.Empty(packageQueue.ListProcesses());
        Assert.False(packageQueue.Cancel(snapshot.ProcessId));
        Assert.Throws<ObjectDisposedException>(() => packageQueue.Enqueue(new BackgroundProcessRequest(
            "Late package work",
            "work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task PackageScopedBackgroundProcessQueue_StartAfterStop_ThrowsObjectDisposedException()
    {
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundProcessQueue("test.package", "Test Package", queue);

        await packageQueue.StopAsync();

        Assert.Throws<ObjectDisposedException>(() => packageQueue.Start());
    }

    private static BackgroundProcessRequest CreateRequest(
        string title,
        string groupKey,
        Func<BackgroundProcessContext, Task> executeAsync,
        bool canCancel = true,
        BackgroundProcessIndicator indicator = BackgroundProcessIndicator.Hidden)
        => new(
            title,
            groupKey,
            indicator,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            canCancel,
            executeAsync);

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.True(condition());
                return;
            }

            await Task.Delay(10);
        }
    }
}
