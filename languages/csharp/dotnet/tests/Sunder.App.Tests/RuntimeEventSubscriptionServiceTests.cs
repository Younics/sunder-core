using System.Threading.Channels;
using Sunder.App.Services;
using Sunder.App.Composition;
using Sunder.Runtime.Contracts;
using Xunit;

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
        long generation)
        => new(
            runtimeInstanceId,
            generation,
            generation,
            RuntimeBootstrapState.Ready,
            [],
            [],
            [],
            []);

    private static PackageUiSnapshotDescriptor CreateSource(string contentHash, long generation)
        => new("agent", PackageSourceKind.Dev, generation, RuntimeContractTestData.AppTarget(), contentHash, $"snapshot-{generation}", $"snapshots/{generation}");

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

        public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SnapshotFailuresRemaining { get; set; }

        public void Publish(RuntimePackageSnapshot snapshot)
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
                []));
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
                [],
                HistoryGap: false));
        }

        public async IAsyncEnumerable<RuntimeEventDescriptor> StreamRuntimeEventsAsync(
            long afterSequenceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamStarted.TrySetResult();
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
            => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>([]);

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
