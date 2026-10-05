using System.Threading.Channels;
using Sunder.App.Services;
using Sunder.App.Composition;
using Sunder.Runtime.Contracts;
using Xunit;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

public sealed class RuntimeEventSubscriptionServiceTests
{
    [Fact]
    public async Task Factory_CreatesIndependentSubscriptionStateForEachStartupAttempt()
    {
        var factory = new RuntimeEventSubscriptionServiceFactory(
            new ThrowingRuntimeClientFactory(),
            new DeveloperLogService());

        await using var first = factory.Create();
        await using var retry = factory.Create();

        Assert.NotSame(first, retry);
    }

    [Fact]
    public async Task ApplySnapshotAsync_FencesStaleGenerationsAndRetiredRuntimeInstances()
    {
        var firstRuntime = Guid.NewGuid();
        var secondRuntime = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var writes = new List<RuntimePackageSnapshot>();
        var impactedWrites = new List<IReadOnlyCollection<string>?>();
        service.InitializePresentation(CreateSnapshot(firstRuntime, 1), [], (snapshot, _, impacted, _) =>
        {
            writes.Add(snapshot);
            impactedWrites.Add(impacted);
            return Task.CompletedTask;
        });

        await service.ApplySnapshotAsync(CreateSnapshot(firstRuntime, 2), [], ["agent"]);
        await service.ApplySnapshotAsync(CreateSnapshot(firstRuntime, 1), [], ["stale"]);
        await service.ApplySnapshotAsync(CreateSnapshot(secondRuntime, 1), [], ["replacement"]);
        await service.ApplySnapshotAsync(CreateSnapshot(firstRuntime, 3), [], ["retired"]);

        Assert.Equal([(firstRuntime, 2L), (secondRuntime, 1L)], writes.Select(write => (write.RuntimeInstanceId, write.SessionGeneration)).ToArray());
        Assert.Contains("agent", Assert.IsAssignableFrom<IReadOnlyCollection<string>>(impactedWrites[0]));
        Assert.Contains("replacement", Assert.IsAssignableFrom<IReadOnlyCollection<string>>(impactedWrites[1]));
    }

    [Fact]
    public async Task ApplySnapshotAsync_SameGenerationUsesMetadataWriterOnly()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(
            new ThrowingRuntimeClientFactory(),
            new DeveloperLogService());
        var lifecycleWrites = 0;
        var metadataWrites = new List<RuntimePackageSnapshot>();
        service.InitializePresentation(
            CreateSnapshot(runtimeInstanceId, 1),
            [],
            (_, _, _, _) =>
            {
                lifecycleWrites++;
                return Task.CompletedTask;
            },
            (snapshot, _) =>
            {
                metadataWrites.Add(snapshot);
                return Task.CompletedTask;
            });
        var metadata = CreateSnapshot(runtimeInstanceId, 1) with { EventSequence = 2 };

        await service.ApplySnapshotAsync(metadata, []);
        await service.ApplySnapshotAsync(metadata, []);

        Assert.Equal(0, lifecycleWrites);
        Assert.Equal([metadata], metadataWrites);
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_SameGenerationRunsOneFullLifecycleWithPackageOnlyRetry()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var snapshot = CreateSnapshot(runtimeInstanceId, 7, [package]);
        var source = CreateSource("hash-a", 7);
        var factory = new StreamingRuntimeClientFactory(snapshot);
        factory.Client.PackageSources = [source];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writes = new List<(RuntimePackageSnapshot Snapshot, IReadOnlyCollection<string>? Retry)>();
        service.InitializePresentation(snapshot, [source], (write, _, retry, _) =>
        {
            writes.Add((write, retry));
            return Task.CompletedTask;
        });

        var outcome = await service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "HASH-A",
            TimeSpan.FromSeconds(2));

        Assert.Equal(AppPackageUiRecoveryPresentationOutcome.Applied, outcome);
        var write = Assert.Single(writes);
        Assert.Equal(7, write.Snapshot.SessionGeneration);
        var retry = Assert.IsType<AppPackageQuarantineRetryIntent>(write.Retry);
        Assert.True(retry.PreserveKnownViewQuarantines);
        Assert.Equal(["agent"], retry);
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_WaitsForExactPackageToReturn()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var active = CreateSnapshot(runtimeInstanceId, 3, [package]);
        var temporarilyRemoved = CreateSnapshot(runtimeInstanceId, 3);
        var source = CreateSource("hash-a", 3);
        var factory = new StreamingRuntimeClientFactory(temporarilyRemoved);
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writes = 0;
        service.InitializePresentation(active, [source], (_, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await factory.Client.SnapshotRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref writes));

        factory.Client.PackageSources = [source];
        factory.Client.SetSnapshot(active with { EventSequence = 4 });
        var outcome = await recovery.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AppPackageUiRecoveryPresentationOutcome.Applied, outcome);
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.True(factory.Client.SnapshotRequestCount >= 2);
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_ContentUpdateOrTimeoutNeverWrites()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var snapshot = CreateSnapshot(runtimeInstanceId, 2, [package]);
        var original = CreateSource("hash-a", 2);
        var updated = CreateSource("hash-b", 2);
        var factory = new StreamingRuntimeClientFactory(snapshot);
        factory.Client.PackageSources = [updated];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writes = 0;
        service.InitializePresentation(snapshot, [original], (_, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        });

        var changed = await service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(1));
        factory.Client.PackageSources = [];
        factory.Client.SetSnapshot(CreateSnapshot(runtimeInstanceId, 2));
        var timedOut = await service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-c",
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(AppPackageUiRecoveryPresentationOutcome.ContentChanged, changed);
        Assert.Equal(AppPackageUiRecoveryPresentationOutcome.TimedOut, timedOut);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_WriterFailureIsNotRequeued()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var snapshot = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var source = CreateSource("hash-a", 1);
        var factory = new StreamingRuntimeClientFactory(snapshot);
        factory.Client.PackageSources = [source];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var attempts = 0;
        service.InitializePresentation(snapshot, [source], (_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("replacement failed");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ForcePackageRecoveryPresentationAsync(
                "agent",
                "hash-a",
                TimeSpan.FromSeconds(1)));
        await Task.Delay(150);

        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_TimeoutAfterAppliedWriterReturnsApplied()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var snapshot = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var source = CreateSource("hash-a", 1);
        var factory = new StreamingRuntimeClientFactory(snapshot);
        factory.Client.PackageSources = [source];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.InitializePresentation(snapshot, [source], async (_, _, _, cancellationToken) =>
        {
            writerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The shell lifecycle commit succeeded before optional window refresh was canceled.
            }
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromMilliseconds(50));
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Applied,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_NormalRuntimePresentationSupersedesRecovery()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var initialSource = CreateSource("hash-a", 1);
        var replacement = CreateSnapshot(runtimeInstanceId, 2, [package]);
        var replacementSource = CreateSource("hash-b", 2);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [initialSource];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var recoveryWriterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var normalWrites = 0;
        service.InitializePresentation(initial, [initialSource], async (_, _, retry, cancellationToken) =>
        {
            if (retry is AppPackageQuarantineRetryIntent)
            {
                recoveryWriterStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }
            Interlocked.Increment(ref normalWrites);
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await recoveryWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var normal = service.ApplySnapshotAsync(replacement, [replacementSource]);

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Superseded,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
        await normal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref normalWrites));
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_SameContentRuntimePresentationCompletesTransferredRecovery()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var initialSource = CreateSource("hash-a", 1);
        var replacement = CreateSnapshot(runtimeInstanceId, 2, [package]);
        var replacementSource = CreateSource("hash-a", 2);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [initialSource];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var recoveryWriterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(long Generation, IReadOnlyCollection<string>? Retry)>();
        service.InitializePresentation(initial, [initialSource], async (snapshot, _, retry, cancellationToken) =>
        {
            writes.Add((snapshot.SessionGeneration, retry));
            if (snapshot.SessionGeneration == 1)
            {
                recoveryWriterStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await recoveryWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var normal = service.ApplySnapshotAsync(replacement, [replacementSource]);

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Applied,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
        await normal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Collection(
            writes,
            forced =>
            {
                Assert.Equal(1, forced.Generation);
                Assert.IsType<AppPackageQuarantineRetryIntent>(forced.Retry);
            },
            transferred =>
            {
                Assert.Equal(2, transferred.Generation);
                var retry = Assert.IsType<AppPackageQuarantineRetryIntent>(
                    transferred.Retry);
                Assert.True(retry.PreserveKnownViewQuarantines);
                Assert.Equal(["agent"], retry);
            });
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_SameContentSupersessionKeepsTransferUntilLatestCommit()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var initialSource = CreateSource("hash-a", 1);
        var second = CreateSnapshot(runtimeInstanceId, 2, [package]);
        var secondSource = CreateSource("hash-a", 2);
        var third = CreateSnapshot(runtimeInstanceId, 3, [package]);
        var thirdSource = CreateSource("hash-a", 3);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [initialSource];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var firstWriterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWriterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(long Generation, IReadOnlyCollection<string>? Retry)>();
        service.InitializePresentation(initial, [initialSource], async (snapshot, _, retry, cancellationToken) =>
        {
            writes.Add((snapshot.SessionGeneration, retry));
            if (snapshot.SessionGeneration == 1)
            {
                firstWriterStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (snapshot.SessionGeneration == 2)
            {
                secondWriterStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await firstWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondPresentation = service.ApplySnapshotAsync(second, [secondSource]);
        await secondWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var thirdPresentation = service.ApplySnapshotAsync(third, [thirdSource]);

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Applied,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.WhenAll(secondPresentation, thirdPresentation)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([1L, 2L, 3L], writes.Select(write => write.Generation));
        Assert.All(writes, write =>
        {
            var retry = Assert.IsType<AppPackageQuarantineRetryIntent>(write.Retry);
            Assert.True(retry.PreserveKnownViewQuarantines);
            Assert.Equal(["agent"], retry);
        });
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_HungRuntimeReadDoesNotBlockUninstallPresentation()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var source = CreateSource("hash-a", 1);
        var uninstalled = CreateSnapshot(runtimeInstanceId, 2);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [source];
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Client.GetRuntimePackageSnapshotAsyncCallback = async _ =>
        {
            readStarted.TrySetResult();
            await releaseRead.Task;
            return initial;
        };
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var normalWrites = 0;
        service.InitializePresentation(initial, [source], (_, _, retry, _) =>
        {
            if (retry is not AppPackageQuarantineRetryIntent)
            {
                Interlocked.Increment(ref normalWrites);
            }
            return Task.CompletedTask;
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromMilliseconds(500));
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Client.PackageSources = [];
        factory.Client.SetSnapshot(uninstalled);

        await service.ApplySnapshotAsync(uninstalled, [])
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref normalWrites));
        factory.Client.GetRuntimePackageSnapshotAsyncCallback = null;
        releaseRead.SetResult();
        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.TimedOut,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_CompletedTransferSurvivesCanceledSnapshotRead()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var replacement = CreateSnapshot(runtimeInstanceId, 2, [package]);
        var laterFailure = CreateSnapshot(runtimeInstanceId, 3, [package]);
        var initialSource = CreateSource("hash-a", 1);
        var replacementSource = CreateSource("hash-a", 2);
        var laterSource = CreateSource("hash-a", 3);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [initialSource];
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Client.GetRuntimePackageSnapshotAsyncCallback = async cancellationToken =>
        {
            readStarted.TrySetResult();
            await releaseRead.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return initial;
        };
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writes = new List<long>();
        service.InitializePresentation(initial, [initialSource], (snapshot, _, _, _) =>
        {
            writes.Add(snapshot.SessionGeneration);
            return snapshot.SessionGeneration == 3
                ? Task.FromException(new InvalidOperationException("later presentation failed"))
                : Task.CompletedTask;
        });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Client.PackageSources = [replacementSource];
        factory.Client.SetSnapshot(replacement);
        await service.ApplySnapshotAsync(replacement, [replacementSource])
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplySnapshotAsync(laterFailure, [laterSource]));

        releaseRead.TrySetResult();

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Applied,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal([2L, 3L], writes);
    }

    [Fact]
    public async Task ForcePackageRecoveryPresentationAsync_SameGenerationMetadataDoesNotPoisonReplacement()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var package = CreateActiveAppPackage();
        var initial = CreateSnapshot(runtimeInstanceId, 1, [package]);
        var source = CreateSource("hash-a", 1);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [source];
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService());
        var writerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataWrites = 0;
        service.InitializePresentation(
            initial,
            [source],
            async (_, _, _, cancellationToken) =>
            {
                writerStarted.TrySetResult();
                await releaseWriter.Task.WaitAsync(cancellationToken);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref metadataWrites);
                return Task.CompletedTask;
            });

        var recovery = service.ForcePackageRecoveryPresentationAsync(
            "agent",
            "hash-a",
            TimeSpan.FromSeconds(2));
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var metadata = service.ApplySnapshotAsync(
            initial with { EventSequence = 2 },
            [source]);
        await Task.Delay(50);
        Assert.False(recovery.IsCompleted);

        releaseWriter.SetResult();

        Assert.Equal(
            AppPackageUiRecoveryPresentationOutcome.Applied,
            await recovery.WaitAsync(TimeSpan.FromSeconds(2)));
        await metadata.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref metadataWrites));
    }

    [Fact]
    public async Task ApplySnapshotAsync_SupersedesPreparingGenerationAndCommitsLatestOnce()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committedGenerations = new List<long>();
        service.InitializePresentation(CreateSnapshot(runtimeInstanceId, 1), [], async (snapshot, _, _, cancellationToken) =>
        {
            if (snapshot.SessionGeneration == 2)
            {
                secondStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    secondCancelled.SetResult();
                    throw;
                }
            }

            committedGenerations.Add(snapshot.SessionGeneration);
        });

        var second = service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 2), []);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var third = service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 3), []);
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(2));

        await secondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([3], committedGenerations);

        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 3), []);
        Assert.Equal([3], committedGenerations);
    }

    [Fact]
    public async Task ApplySnapshotAsync_FailedGenerationRemainsRetryable()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var attempts = 0;
        service.InitializePresentation(CreateSnapshot(runtimeInstanceId, 1), [], (snapshot, _, _, _) =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("presentation failed"))
                : Task.CompletedTask;
        });
        var snapshot = CreateSnapshot(runtimeInstanceId, 2);
        var waiter = service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySnapshotAsync(snapshot, []));

        Assert.Equal("presentation failed", exception.Message);
        var waiterException = await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);
        Assert.Contains("generation 2", waiterException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("presentation failed", waiterException.Message, StringComparison.OrdinalIgnoreCase);
        await service.ApplySnapshotAsync(snapshot, []);
        Assert.Equal(2, attempts);
        await service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2));
    }

    [Fact]
    public async Task PresentationHistory_PrunesTerminalOutcomesAndRetiredRuntimeInstances()
    {
        var initialRuntime = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        service.InitializePresentation(CreateSnapshot(initialRuntime, 1), [], (_, _, _, _) => Task.CompletedTask);

        for (var index = 0; index < 80; index++)
        {
            var failed = new RuntimePackageSnapshot(
                Guid.NewGuid(),
                index + 1,
                index + 1,
                RuntimeBootstrapState.Failed,
                [],
                [],
                [],
                ["bootstrap failed"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySnapshotAsync(failed, []));
        }

        var currentRuntime = initialRuntime;
        for (var index = 0; index < 12; index++)
        {
            currentRuntime = Guid.NewGuid();
            await service.ApplySnapshotAsync(CreateSnapshot(currentRuntime, 1), []);
        }

        Assert.InRange(service.TerminalOutcomeCount, 0, 64);
        Assert.InRange(service.RetiredRuntimeInstanceCount, 0, 8);
    }

    [Fact]
    public async Task PresentationWorker_TransientSnapshotFailureDoesNotRejectGeneration()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(runtimeInstanceId, 1);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.SnapshotFailuresRemaining = 1;
        await using var service = new RuntimeEventSubscriptionService(factory, new DeveloperLogService());
        await service.StartAsync(initial, [], (_, _, _, _) => Task.CompletedTask);
        service.ReleasePresentation();
        await factory.Client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        factory.Client.Publish(CreateSnapshot(runtimeInstanceId, 2));

        await service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2))
            .WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ApplySnapshotAsync_RetriesDisabledPackageOnlyForChangedContentOrExplicitRetry()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var retries = new List<IReadOnlyCollection<string>?>();
        service.InitializePresentation(
            CreateSnapshot(runtimeInstanceId, 1),
            [CreateSource("hash-a", 1)],
            (_, _, retryDisabledPackageIds, _) =>
            {
                retries.Add(retryDisabledPackageIds);
                return Task.CompletedTask;
            });

        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 2), [CreateSource("hash-a", 2)]);
        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 3), [CreateSource("hash-b", 3)]);
        await service.ApplySnapshotAsync(
            CreateSnapshot(runtimeInstanceId, 4),
            [CreateSource("hash-b", 4)],
            ["agent"]);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(retries[0]));
        Assert.Contains("agent", Assert.IsAssignableFrom<IReadOnlyCollection<string>>(retries[1]));
        Assert.Contains("agent", Assert.IsAssignableFrom<IReadOnlyCollection<string>>(retries[2]));
    }

    [Fact]
    public async Task ApplySnapshotAsync_FailedReplacementDoesNotRetireAppliedRuntime()
    {
        var firstRuntime = Guid.NewGuid();
        var secondRuntime = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var attemptedStamps = new List<(Guid RuntimeInstanceId, long Generation)>();
        service.InitializePresentation(CreateSnapshot(firstRuntime, 1), [], (snapshot, _, _, _) =>
        {
            attemptedStamps.Add((snapshot.RuntimeInstanceId, snapshot.SessionGeneration));
            return snapshot.RuntimeInstanceId == secondRuntime
                ? Task.FromException(new InvalidOperationException("replacement failed"))
                : Task.CompletedTask;
        });
        var oldRuntimeWaiter = service.WaitUntilAppliedAsync(new RuntimePackageStamp(firstRuntime, 2));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplySnapshotAsync(CreateSnapshot(secondRuntime, 1), []));

        Assert.False(oldRuntimeWaiter.IsCompleted);
        await service.ApplySnapshotAsync(CreateSnapshot(firstRuntime, 2), []);
        await oldRuntimeWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([(secondRuntime, 1L), (firstRuntime, 2L)], attemptedStamps);
    }

    [Fact]
    public async Task ApplySnapshotAsync_SupersessionAtWriterCompletionStillCommitsAppliedStamp()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visibleGenerations = new List<long>();
        Task? third = null;
        service.InitializePresentation(CreateSnapshot(runtimeInstanceId, 1), [], async (snapshot, _, _, cancellationToken) =>
        {
            if (snapshot.SessionGeneration == 2)
            {
                visibleGenerations.Add(2);
                third = service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 3), []);
                return;
            }

            thirdStarted.SetResult();
            await releaseThird.Task.WaitAsync(cancellationToken);
            visibleGenerations.Add(3);
        });
        var secondWaiter = service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2));

        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 2), []);
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await secondWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([2], visibleGenerations);

        releaseThird.SetResult();
        Assert.NotNull(third);
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([2, 3], visibleGenerations);
    }

    [Fact]
    public async Task StartAsync_HoldsCatchUpPresentationUntilInitialShellIsReleased()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(runtimeInstanceId, 1);
        var factory = new StreamingRuntimeClientFactory(initial);
        await using var service = new RuntimeEventSubscriptionService(factory, new DeveloperLogService());
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await service.StartAsync(
            initial,
            [],
            (_, _, _, _) =>
            {
                writerStarted.TrySetResult();
                return Task.CompletedTask;
            });
        await factory.Client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        factory.Client.Publish(CreateSnapshot(runtimeInstanceId, 2));
        await Task.Delay(50);

        Assert.False(writerStarted.Task.IsCompleted);
        service.ReleasePresentation();
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StartAsync_ProductionReaderSupersedesBlockedPresentationWithLatestGeneration()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(runtimeInstanceId, 1);
        var factory = new StreamingRuntimeClientFactory(initial);
        await using var service = new RuntimeEventSubscriptionService(factory, new DeveloperLogService());
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committedGenerations = new List<long>();
        await service.StartAsync(
            initial,
            [],
            async (snapshot, _, _, cancellationToken) =>
            {
                if (snapshot.SessionGeneration == 2)
                {
                    secondStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        secondCancelled.TrySetResult();
                        throw;
                    }
                }

                committedGenerations.Add(snapshot.SessionGeneration);
            });
        service.ReleasePresentation();
        await factory.Client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        factory.Client.Publish(CreateSnapshot(runtimeInstanceId, 2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Client.Publish(CreateSnapshot(runtimeInstanceId, 3));

        await secondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 3)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([3], committedGenerations);
    }

    [Fact]
    public async Task SessionGenerationPackageIds_DoNotExplicitlyRetryAppQuarantines()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(runtimeInstanceId, 1);
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.PackageSources = [CreateSource("hash-a", 2)];
        await using var service = new RuntimeEventSubscriptionService(factory, new DeveloperLogService());
        IReadOnlyCollection<string>? retriedPackageIds = null;
        await service.StartAsync(
            initial,
            [CreateSource("hash-a", 1)],
            (_, _, retryDisabledPackageIds, _) =>
            {
                retriedPackageIds = retryDisabledPackageIds;
                return Task.CompletedTask;
            });
        service.ReleasePresentation();
        await factory.Client.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        factory.Client.Publish(CreateSnapshot(runtimeInstanceId, 2), ["agent"]);

        await service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(retriedPackageIds));
    }

    [Fact]
    public async Task ProductionReader_HistoryGapReconnectSuppressesReplayAndNotifiesNextLiveRecoveryOnce()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(runtimeInstanceId, 1);
        var catchUp = initial with { EventSequence = 5 };
        var factory = new StreamingRuntimeClientFactory(initial);
        factory.Client.SetSnapshot(catchUp);
        factory.Client.HistoryGap = true;
        factory.Client.ReplayEvents =
        [
            CreateRecoveryEvent(runtimeInstanceId, 4, RuntimeEventKind.PackageRecoverySuspended),
            CreateRecoveryEvent(runtimeInstanceId, 5, RuntimeEventKind.PackageRecoverySucceeded),
        ];
        factory.Client.DisconnectStreamsRemaining = 1;
        var notifications = new NotificationCenterService(
            Path.Combine(CreateTempDirectory(), "notifications.json"));
        var recoveryNotifications = new RuntimePackageRecoveryNotificationService(notifications);
        var toastCount = 0;
        notifications.ToastQueued += _ => Interlocked.Increment(ref toastCount);
        await using var service = new RuntimeEventSubscriptionService(
            factory,
            new DeveloperLogService(),
            recoveryNotifications);
        await service.StartAsync(initial, [], (_, _, _, _) => Task.CompletedTask);
        service.ReleasePresentation();

        await WaitUntilAsync(() => factory.Client.StreamStartCount >= 2);
        Assert.Equal(0, Volatile.Read(ref toastCount));
        Assert.Empty(notifications.ListNotifications());

        var live = CreateRecoveryEvent(
            runtimeInstanceId,
            6,
            RuntimeEventKind.PackageRecoveryStarted);
        factory.Client.PublishEvent(live);
        factory.Client.PublishEvent(live);
        await WaitUntilAsync(() => Volatile.Read(ref toastCount) == 1);

        Assert.Equal(1, Volatile.Read(ref toastCount));
        Assert.Empty(notifications.ListNotifications());
    }

    [Fact]
    public async Task WaitUntilAppliedAsync_CompletesForExactAndNewerGeneration()
    {
        var runtimeInstanceId = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        service.InitializePresentation(CreateSnapshot(runtimeInstanceId, 1), [], (_, _, _, _) => Task.CompletedTask);

        var exact = service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 2));
        Assert.False(exact.IsCompleted);
        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 2), []);
        await exact.WaitAsync(TimeSpan.FromSeconds(2));

        var superseded = service.WaitUntilAppliedAsync(new RuntimePackageStamp(runtimeInstanceId, 3));
        Assert.False(superseded.IsCompleted);
        await service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 4), []);
        await superseded.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitUntilAppliedAsync_ResetsGenerationOnRuntimeInstanceReplacement()
    {
        var firstRuntime = Guid.NewGuid();
        var secondRuntime = Guid.NewGuid();
        await using var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        service.InitializePresentation(CreateSnapshot(firstRuntime, 10), [], (_, _, _, _) => Task.CompletedTask);

        var oldRuntimeWaiter = service.WaitUntilAppliedAsync(new RuntimePackageStamp(firstRuntime, 11));
        await service.ApplySnapshotAsync(CreateSnapshot(secondRuntime, 1), []);
        await oldRuntimeWaiter.WaitAsync(TimeSpan.FromSeconds(2));

        var replacementWaiter = service.WaitUntilAppliedAsync(new RuntimePackageStamp(secondRuntime, 2));
        Assert.False(replacementWaiter.IsCompleted);
        await service.ApplySnapshotAsync(CreateSnapshot(secondRuntime, 2), []);
        await replacementWaiter.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForActivePresentationWriter()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var service = new RuntimeEventSubscriptionService(new ThrowingRuntimeClientFactory(), new DeveloperLogService());
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.InitializePresentation(CreateSnapshot(runtimeInstanceId, 1), [], async (_, _, _, _) =>
        {
            writerStarted.SetResult();
            await releaseWriter.Task;
        });

        var apply = service.ApplySnapshotAsync(CreateSnapshot(runtimeInstanceId, 2), []);
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dispose = service.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            releaseWriter.TrySetResult();
            await Task.WhenAll(apply, dispose).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static RuntimePackageSnapshot CreateSnapshot(
        Guid runtimeInstanceId,
        long generation,
        IReadOnlyList<ActivePackageDescriptor>? activePackages = null)
        => new(
            runtimeInstanceId,
            generation,
            generation,
            RuntimeBootstrapState.Ready,
            activePackages ?? [],
            [],
            [],
            []);

    private static ActivePackageDescriptor CreateActiveAppPackage()
        => new(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: []);

    private static PackageUiSnapshotDescriptor CreateSource(string contentHash, long generation)
        => new("agent", PackageSourceKind.Dev, generation, RuntimeContractTestData.AppTarget(), contentHash, $"snapshot-{generation}", $"snapshots/{generation}");

    private static RuntimeEventDescriptor CreateRecoveryEvent(
        Guid runtimeInstanceId,
        long sequenceId,
        RuntimeEventKind kind)
        => new(
            runtimeInstanceId,
            sequenceId,
            DateTimeOffset.UtcNow,
            kind,
            SessionGeneration: 1,
            RuntimeOperationPhase.Idle,
            RuntimeBootstrapState.Ready,
            ["test.package"],
            kind == RuntimeEventKind.PackageRecoverySucceeded
                ? true
                : kind == RuntimeEventKind.PackageRecoverySuspended
                    ? false
                    : null,
            $"Recovery event {kind}.");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected subscription condition was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class ThrowingRuntimeClientFactory : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => throw new InvalidOperationException("Runtime API is not used by this writer test.");
    }

    private sealed class StreamingRuntimeClientFactory(RuntimePackageSnapshot initialSnapshot) : IRuntimeApiClientFactory
    {
        public StreamingRuntimeClient Client { get; } = new(initialSnapshot);

        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => typeof(TClient) == typeof(IRuntimeEventClient) || typeof(TClient) == typeof(IRuntimeShellClient)
                ? (TClient)(object)Client
                : throw new InvalidOperationException($"Unexpected Runtime client type {typeof(TClient).Name}.");
    }

    private sealed class StreamingRuntimeClient(RuntimePackageSnapshot initialSnapshot) : IRuntimeEventClient, IRuntimeShellClient
    {
        private readonly Channel<RuntimeEventDescriptor> _events = Channel.CreateUnbounded<RuntimeEventDescriptor>();
        private RuntimePackageSnapshot _snapshot = initialSnapshot;
        private int _streamStartCount;
        private int _snapshotRequestCount;

        public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SnapshotRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SnapshotFailuresRemaining { get; set; }

        public Func<CancellationToken, Task<RuntimePackageSnapshot>>?
            GetRuntimePackageSnapshotAsyncCallback
        { get; set; }

        public IReadOnlyList<PackageUiSnapshotDescriptor> PackageSources { get; set; } = [];

        public IReadOnlyList<RuntimeEventDescriptor> ReplayEvents { get; set; } = [];

        public bool HistoryGap { get; set; }

        public int DisconnectStreamsRemaining { get; set; }

        public int StreamStartCount => Volatile.Read(ref _streamStartCount);

        public int SnapshotRequestCount => Volatile.Read(ref _snapshotRequestCount);

        public void SetSnapshot(RuntimePackageSnapshot snapshot)
            => Volatile.Write(ref _snapshot, snapshot);

        public void PublishEvent(RuntimeEventDescriptor runtimeEvent)
            => _events.Writer.TryWrite(runtimeEvent);

        public void Publish(
            RuntimePackageSnapshot snapshot,
            IReadOnlyList<string>? packageIds = null)
        {
            Volatile.Write(ref _snapshot, snapshot);
            _events.Writer.TryWrite(new RuntimeEventDescriptor(
                snapshot.RuntimeInstanceId,
                snapshot.EventSequence,
                DateTimeOffset.UtcNow,
                RuntimeEventKind.SessionGenerationChanged,
                snapshot.SessionGeneration,
                RuntimeOperationPhase.Idle,
                snapshot.BootstrapState,
                packageIds ?? []));
        }

        public Task<RuntimeEventSnapshot> GetRuntimeEventSnapshotAsync(
            long afterSequenceId = 0,
            CancellationToken cancellationToken = default)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return Task.FromResult(new RuntimeEventSnapshot(
                snapshot.RuntimeInstanceId,
                snapshot.EventSequence,
                snapshot.SessionGeneration,
                RuntimeOperationPhase.Idle,
                snapshot.BootstrapState,
                [],
                ReplayEvents,
                HistoryGap));
        }

        public async IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(
            long afterSequenceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _streamStartCount);
            StreamStarted.TrySetResult();
            if (DisconnectStreamsRemaining > 0)
            {
                DisconnectStreamsRemaining--;
                throw new HttpRequestException("injected stream disconnect");
            }
            await foreach (var runtimeEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                if (runtimeEvent.SequenceId > afterSequenceId)
                {
                    yield return runtimeEvent;
                }
            }
        }

        public Task<RuntimePackageSnapshot> GetRuntimePackageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _snapshotRequestCount);
            SnapshotRequested.TrySetResult();
            if (GetRuntimePackageSnapshotAsyncCallback is not null)
            {
                return GetRuntimePackageSnapshotAsyncCallback(cancellationToken);
            }
            if (SnapshotFailuresRemaining > 0)
            {
                SnapshotFailuresRemaining--;
                throw new HttpRequestException("transient snapshot failure");
            }
            return Task.FromResult(Volatile.Read(ref _snapshot));
        }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
            string appRid,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSources);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetStagedPackageUiSnapshotsAsync(
            string stageId,
            string appRid,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

        public Task DownloadPackageUiSnapshotAsync(
            PackageUiSnapshotDescriptor snapshot,
            Stream destination,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => new($"https://runtime.test/packages/{packageId}/assets/{assetPath}");

        public void Dispose()
        {
        }
    }
}
