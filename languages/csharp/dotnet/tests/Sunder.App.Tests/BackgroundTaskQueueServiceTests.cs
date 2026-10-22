using System.Collections.Concurrent;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using Xunit;

namespace Sunder.App.Tests;

public sealed class BackgroundTaskQueueServiceTests
{
    [Fact]
    public async Task QueuedSuccessorUsesItsOwnSubmissionContextIncludingSuppressedFlow()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var authority = new AsyncLocal<string?>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<string?>();
        authority.Value = "first";
        queue.Enqueue(CreateRequest("first", "work", async _ =>
        {
            observed.Enqueue(authority.Value);
            authority.Value = "first-internal-authority";
            started.SetResult();
            await release.Task;
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        authority.Value = "second";
        queue.Enqueue(CreateRequest("second", "work", async _ =>
        {
            await Task.Yield();
            observed.Enqueue(authority.Value);
        }));
        using (ExecutionContext.SuppressFlow())
            queue.Enqueue(CreateRequest("suppressed", "work", _ =>
            {
                observed.Enqueue(authority.Value);
                return Task.CompletedTask;
            }));
        authority.Value = "caller-after-enqueue";
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(task => task.IsTerminal));
        Assert.Equal(new string?[] { "first", "second", null }, observed.ToArray());
        Assert.Equal("caller-after-enqueue", authority.Value);
    }

    [Fact]
    public async Task ForeignCancellationRacingTaskCancellationRemainsAFailure()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = queue.Enqueue(CreateRequest("foreign-cancellation", "work", async _ =>
        {
            started.SetResult();
            await release.Task;
            throw new OperationCanceledException("Callback did not cancel on the task's token.", new CancellationToken(true));
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(queue.Cancel(task.TaskId));
        release.SetResult();
        await WaitForConditionAsync(() => queue.GetTask(task.TaskId)?.IsTerminal == true);
        Assert.Equal(BackgroundTaskState.Failed, queue.GetTask(task.TaskId)!.State);
    }

    [Fact]
    public async Task Enqueue_WithSequentialGroup_BlocksSameGroupButRunsOtherGroups()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
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
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task ReportProgress_UpdatesSnapshotStatusAndProgress()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var snapshots = new ConcurrentQueue<BackgroundTaskSnapshot>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TaskChanged += (_, e) => snapshots.Enqueue(e.Snapshot);

        queue.Enqueue(CreateRequest("progress", "work", async context =>
        {
            context.ReportProgress(42, "Halfway there");
            await release.Task;
        }));

        await WaitForConditionAsync(() => snapshots.Any(snapshot => snapshot.ProgressPercent == 42 && snapshot.StatusText == "Halfway there"));
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task ReportProgress_WithNonFiniteValue_FailsProcess(double progress)
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);

        var process = queue.Enqueue(CreateRequest("progress", "work", context =>
        {
            context.ReportProgress(progress);
            return Task.CompletedTask;
        }));

        await WaitForConditionAsync(() => queue.GetTask(process.TaskId)?.IsTerminal == true);
        var failed = queue.GetTask(process.TaskId);
        Assert.Equal(BackgroundTaskState.Failed, failed?.State);
        Assert.False(failed?.CanCancel);
    }

    [Fact]
    public async Task Snapshots_FreezeMetadataAndOnlyAdvertiseActionableCancellation()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["item"] = "original",
        };
        var request = CreateRequest("metadata", "work", _ => Task.CompletedTask) with
        {
            Metadata = metadata,
        };

        metadata["item"] = "changed";
        var queued = queue.Enqueue(request);
        Assert.True(queued.CanCancel);
        Assert.Equal("original", queued.Metadata["ITEM"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)queued.Metadata).Clear());

        await WaitForConditionAsync(() => queue.GetTask(queued.TaskId)?.IsTerminal == true);
        var completed = queue.GetTask(queued.TaskId);
        Assert.NotNull(completed);
        Assert.False(completed.CanCancel);
        Assert.Equal("original", completed.Metadata["ITEM"]);
    }

    [Fact]
    public void Enqueue_RejectsNullDelegateAndUnknownEnumValues()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(CreateRequest("invalid", "work", null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(CreateRequest("invalid", "work", _ => Task.CompletedTask) with
        {
            Indicator = (BackgroundTaskIndicator)99,
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(CreateRequest("invalid", "work", _ => Task.CompletedTask) with
        {
            ConcurrencyMode = (BackgroundTaskConcurrencyMode)99,
        }));
    }

    [Fact]
    public void TaskChanged_WhenSubscriberThrows_ContinuesNotifyingRemainingSubscribers()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var receivedQueuedSnapshot = false;

        queue.TaskChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        queue.TaskChanged += (_, e) =>
        {
            if (e.Snapshot.State == BackgroundTaskState.Queued)
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
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        Assert.True(queue.Cancel(queued.TaskId));

        var cancelled = queue.GetTask(queued.TaskId);
        Assert.Equal(BackgroundTaskState.Cancelled, cancelled?.State);
        Assert.False(cancelled?.CanCancel);
        Assert.DoesNotContain("b", started);

        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task CancelAllAsync_CancelsRunningAndQueuedProcessesRegardlessOfCanCancel()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        queue.Enqueue(CreateRequest("a", "work", async context =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
        }, canCancel: false));
        var queued = queue.Enqueue(CreateRequest("b", "work", _ => Task.CompletedTask, canCancel: false));

        await WaitForConditionAsync(() => queue.ListTasks().Any(process => process.State == BackgroundTaskState.Running));
        await queue.CancelAllAsync();

        Assert.All(queue.ListTasks(), process => Assert.Equal(BackgroundTaskState.Cancelled, process.State));
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(queued.TaskId)?.State);
    }

    [Fact]
    public async Task DisposeAsync_CancelsQueuedAndRunningProcessesAndWaitsForCleanup()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        Assert.Equal(BackgroundTaskState.Cancelling, queue.GetTask(running.TaskId)?.State);
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(queued.TaskId)?.State);
        Assert.False(queuedStarted);

        allowCleanup.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(queue.ListTasks(), process => Assert.Equal(BackgroundTaskState.Cancelled, process.State));
    }

    [Fact]
    public async Task Dispose_CancelsRunningProcessesWithoutWaitingForCleanup()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
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
        Assert.Equal(BackgroundTaskState.Cancelling, queue.GetTask(running.TaskId)?.State);

        allowCleanup.SetResult();
        await WaitForConditionAsync(() => queue.GetTask(running.TaskId)?.State == BackgroundTaskState.Cancelled);
    }

    [Fact]
    public void Enqueue_AfterDispose_ThrowsObjectDisposedException()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        queue.Dispose();

        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(CreateRequest("after dispose", "work", _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task Cancellation_AfterDispose_IsSafeNoOp()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        queue.Dispose();

        Assert.False(queue.Cancel(Guid.NewGuid()));
        await queue.CancelAllAsync();
    }

    [Fact]
    public async Task Cancel_WhenRunningProcessReturnsAfterCancellation_MarksCancelled()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = queue.Enqueue(CreateRequest("cancel", "work", async _ =>
        {
            started.SetResult();
            await release.Task;
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(queue.Cancel(process.TaskId));
        release.SetResult();

        await WaitForConditionAsync(() => queue.GetTask(process.TaskId)?.IsTerminal == true);
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(process.TaskId)?.State);
    }

    [Fact]
    public void Enqueue_WhenHiddenIndicatorIsSet_ExposesHiddenSnapshot()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);

        var process = queue.Enqueue(CreateRequest("hidden", "work", _ => Task.CompletedTask, indicator: BackgroundTaskIndicator.Hidden));

        Assert.Equal(BackgroundTaskIndicator.Hidden, process.Indicator);
        Assert.Equal(BackgroundTaskIndicator.Hidden, queue.GetTask(process.TaskId)?.Indicator);
    }

    [Fact]
    public async Task CompletedHistory_TrimsToConfiguredMaximum()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1, maxCompletedHistory: 2);

        queue.Enqueue(CreateRequest("a", "work-a", _ => Task.CompletedTask));
        queue.Enqueue(CreateRequest("b", "work-b", _ => Task.CompletedTask));
        queue.Enqueue(CreateRequest("c", "work-c", _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.ListTasks().Count == 2 && queue.ListTasks().All(process => process.IsTerminal));

        var titles = queue.ListTasks().Select(process => process.Title).ToArray();
        Assert.DoesNotContain("a", titles);
        Assert.Contains("b", titles);
        Assert.Contains("c", titles);
    }

    [Fact]
    public async Task CompletedHistory_WhenMaximumIsZero_RemovesTerminalProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1, maxCompletedHistory: 0);

        queue.Enqueue(CreateRequest("removed", "work", _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.ListTasks().Count == 0);
    }

    [Fact]
    public async Task Enqueue_WhenProcessFails_PublishesFailedSnapshotWithErrorMessage()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var snapshots = new ConcurrentQueue<BackgroundTaskSnapshot>();
        queue.TaskChanged += (_, e) => snapshots.Enqueue(e.Snapshot);

        var process = queue.Enqueue(CreateRequest("failure", "work", _ => throw new InvalidOperationException("boom")));

        await WaitForConditionAsync(() => queue.GetTask(process.TaskId)?.State == BackgroundTaskState.Failed);

        var failed = queue.GetTask(process.TaskId);
        Assert.Equal("boom", failed?.ErrorMessage);
        Assert.Contains(snapshots, snapshot => snapshot.TaskId == process.TaskId
                                              && snapshot.State == BackgroundTaskState.Failed
                                              && snapshot.ErrorMessage == "boom");
    }

    [Fact]
    public async Task BackgroundTaskMonitor_WithMainIndicatorFilter_ExcludesHiddenProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(CreateRequest("hidden", "hidden-work", async _ => await release.Task, indicator: BackgroundTaskIndicator.Hidden));
        queue.Enqueue(CreateRequest("visible", "visible-work", async _ => await release.Task, indicator: BackgroundTaskIndicator.Main));

        await WaitForConditionAsync(() => queue.ListTasks().Count(process => process.State == BackgroundTaskState.Running) == 2);
        using var monitor = new BackgroundTaskMonitorViewModel(queue, BackgroundTaskIndicator.Main);

        Assert.Collection(monitor.Processes, process => Assert.Equal("visible", process.Title));

        monitor.Dispose();
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task BackgroundTaskMonitor_WithPackageIndicatorFilter_IncludesOnlyPackageProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(CreateRequest("package", PackageOperationService.PackageStoreGroupKey, async _ => await release.Task, indicator: BackgroundTaskIndicator.Packages));
        queue.Enqueue(CreateRequest("visible", "other", async _ => await release.Task, indicator: BackgroundTaskIndicator.Main));

        await WaitForConditionAsync(() => queue.ListTasks().Count(process => process.State == BackgroundTaskState.Running) == 2);
        using var monitor = new BackgroundTaskMonitorViewModel(
            queue,
            BackgroundTaskIndicator.Packages);

        Assert.Collection(monitor.Processes, process => Assert.Equal("package", process.Title));

        monitor.Dispose();
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_MapsPackageProcessesAndPublishesChanges()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        using var packageQueue = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue);
        packageQueue.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<BackgroundTaskSnapshot>();
        packageQueue.TaskChanged += (_, e) => changes.Enqueue(e.Snapshot);

        var snapshot = packageQueue.Enqueue(new BackgroundTaskRequest(
            "Pull image",
            "docker-image-pulls",
            BackgroundTaskIndicator.Settings,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
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

        Assert.Equal(BackgroundTaskIndicator.Settings, snapshot.Indicator);
        Assert.Equal("docker-image-pulls", snapshot.GroupKey);
        Assert.Equal("agent0ai/agent-zero:latest", snapshot.Metadata["imageReference"]);
        await WaitForConditionAsync(() => changes.Any(change => change.StatusText == "Pulling layer"));

        var appSnapshot = Assert.Single(queue.ListTasks());
        Assert.Equal("package:test.package:docker-image-pulls", appSnapshot.GroupKey);
        Assert.True(PackageScopedBackgroundTaskMetadata.TryCreate(appSnapshot.Metadata, out var packageMetadata));
        Assert.Equal("Test Package", packageMetadata.PackageDisplayName);

        var listed = Assert.Single(packageQueue.ListTasks("docker-image-pulls"));
        Assert.Equal(snapshot.TaskId, listed.TaskId);

        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_Start_WhenCalledTwice_DoesNotDuplicateChangeEvents()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        using var packageQueue = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue);
        packageQueue.Start();
        packageQueue.Start();
        var queuedChangeCount = 0;
        packageQueue.TaskChanged += (_, e) =>
        {
            if (e.Snapshot.State == BackgroundTaskState.Queued)
            {
                queuedChangeCount++;
            }
        };

        var snapshot = packageQueue.Enqueue(new BackgroundTaskRequest(
            "Package work",
            "work",
            BackgroundTaskIndicator.Settings,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask));

        Assert.Equal(1, queuedChangeCount);
        await WaitForConditionAsync(() => queue.GetTask(snapshot.TaskId)?.IsTerminal == true);
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_StopAsync_CancelsAndAwaitsRunningProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var snapshot = packageQueue.Enqueue(new BackgroundTaskRequest(
            "Long package work",
            "work",
            BackgroundTaskIndicator.Settings,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
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
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(snapshot.TaskId)?.State);
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_AfterStop_RejectsNewWorkAndHidesPackageProcesses()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue);
        var snapshot = packageQueue.Enqueue(new BackgroundTaskRequest(
            "Package work",
            "work",
            BackgroundTaskIndicator.Settings,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask));

        await WaitForConditionAsync(() => queue.GetTask(snapshot.TaskId)?.IsTerminal == true);
        await packageQueue.StopAsync();

        Assert.Empty(packageQueue.ListTasks());
        Assert.False(packageQueue.Cancel(snapshot.TaskId));
        Assert.Throws<ObjectDisposedException>(() => packageQueue.Enqueue(new BackgroundTaskRequest(
            "Late package work",
            "work",
            BackgroundTaskIndicator.Settings,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_StartAfterStop_ThrowsObjectDisposedException()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue);

        await packageQueue.StopAsync();

        Assert.Throws<ObjectDisposedException>(() => packageQueue.Start());
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_TwoGenerationsIsolateListsEventsAndCancellation()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        await using var first = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue, firstOwner);
        await using var second = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue, secondOwner);
        first.Start();
        second.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChanges = new ConcurrentQueue<Guid>();
        var secondChanges = new ConcurrentQueue<Guid>();
        first.TaskChanged += (_, e) => firstChanges.Enqueue(e.Snapshot.TaskId);
        second.TaskChanged += (_, e) => secondChanges.Enqueue(e.Snapshot.TaskId);

        var firstProcess = first.Enqueue(CreateRequest("first generation", "work", async _ => await release.Task));
        var secondProcess = second.Enqueue(CreateRequest("second generation", "work", async _ => await release.Task));

        await WaitForConditionAsync(() => queue.ListTasks().Count(process => process.State == BackgroundTaskState.Running) == 2);
        Assert.Collection(first.ListTasks(), process => Assert.Equal(firstProcess.TaskId, process.TaskId));
        Assert.Collection(second.ListTasks(), process => Assert.Equal(secondProcess.TaskId, process.TaskId));
        Assert.All(firstChanges, processId => Assert.Equal(firstProcess.TaskId, processId));
        Assert.All(secondChanges, processId => Assert.Equal(secondProcess.TaskId, processId));
        Assert.False(first.Cancel(secondProcess.TaskId));

        release.SetResult();
        await WaitForConditionAsync(() => queue.ListTasks().All(process => process.IsTerminal));
    }

    [Fact]
    public async Task PackageScopedBackgroundTaskQueue_OwnerStopDoesNotCancelOtherGenerationButPackageAllDoes()
    {
        var queue = new BackgroundTaskQueueService(maxParallelism: 2);
        await using var first = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue, Guid.NewGuid());
        await using var second = new PackageScopedBackgroundTaskQueue("test.package", "Test Package", queue, Guid.NewGuid());
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstProcess = first.Enqueue(CreateRequest("first generation", "first", async context =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            finally
            {
                firstCancelled.TrySetResult();
            }
        }));
        var secondProcess = second.Enqueue(CreateRequest("second generation", "second", async context =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            finally
            {
                secondCancelled.TrySetResult();
            }
        }));

        await WaitForConditionAsync(() => queue.ListTasks().Count(process => process.State == BackgroundTaskState.Running) == 2);
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(firstCancelled.Task.IsCompletedSuccessfully);
        Assert.False(secondCancelled.Task.IsCompleted);
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(firstProcess.TaskId)?.State);
        Assert.Equal(BackgroundTaskState.Running, queue.GetTask(secondProcess.TaskId)?.State);

        await queue.CancelAllPackageProcessesAsync("test.package").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(secondCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(BackgroundTaskState.Cancelled, queue.GetTask(secondProcess.TaskId)?.State);
    }

    private static BackgroundTaskRequest CreateRequest(
        string title,
        string groupKey,
        Func<BackgroundTaskContext, Task> executeAsync,
        bool canCancel = true,
        BackgroundTaskIndicator indicator = BackgroundTaskIndicator.Hidden)
        => new(
            title,
            groupKey,
            indicator,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            canCancel,
            executeAsync);

}
