using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class ActivePackageSessionTests
{
    [Fact]
    public void CreateEmpty_ReturnsFreshExplicitlyEmptySessions()
    {
        var first = ActivePackageSession.CreateEmpty();
        var second = ActivePackageSession.CreateEmpty();
        var regular = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

        Assert.True(first.IsEmpty);
        Assert.True(second.IsEmpty);
        Assert.NotSame(first, second);
        Assert.False(regular.IsEmpty);
    }

    [Fact]
    public void MarkPackageFailed_DisablesSessionPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var marked = session.MarkPackageFailed(
            "test.package",
            PackageFailureOrigin.RuntimeActivation,
            "Activation failed.",
            out var packageToDeactivate);

        Assert.True(marked);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetActivePackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));

        var failedPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(failedPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Failed, failedPackage.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeActivation, failedPackage.FailureOrigin);
        Assert.Equal("Activation failed.", failedPackage.LastError);
        Assert.Equal(1, failedPackage.FailureCount);
        Assert.NotNull(failedPackage.LastFailureAtUtc);
    }

    [Fact]
    public void DisableInstalledPackage_DisablesPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var disabled = session.DisableInstalledPackage("test.package", out var packageToDeactivate);

        Assert.True(disabled);
        Assert.Same(loadedPackage, packageToDeactivate);
        var sessionPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(sessionPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Disabled, sessionPackage.Readiness);
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public void RemovePackage_RemovesSessionAndLoadedPackage()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var removed = session.RemovePackage("test.package", out var packageToDeactivate);

        Assert.True(removed);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetSessionPackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public async Task DisposeAsync_ReleasesSessionFolder()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        try
        {
            var session = new ActivePackageSession(
                sessionFolder,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

            await session.DisposeAsync();

            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_CommitsGenerationOnlyAfterBackgroundServicesStart()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var publisher = new PackageSessionPublisher(owner, NullLogger<PackageSessionPublisher>.Instance);
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundService = new GenerationAwareBackgroundService(
            () => owner.Generation,
            startEntered,
            allowStart.Task);
        var loadedPackage = CreateLoadedPackage("test.package", backgroundService);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            workersStarted: false);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            Assert.Equal(0, backgroundService.StartCount);
            Assert.Equal(0, owner.Generation);

            var commit = publisher.CommitAsync(pending);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            allowStart.TrySetResult();
            var committed = await commit;

            Assert.Equal(1, committed.Stamp.SessionGeneration);
            Assert.Equal(1, backgroundService.StartCount);
            Assert.Equal(0, backgroundService.GenerationObservedAtStart);
            Assert.Equal(1, backgroundService.CommitCount);
            Assert.Equal(0, backgroundService.GenerationObservedAtCommit);
            Assert.Equal(1, backgroundService.CommittedGeneration?.SessionGeneration);
            Assert.Equal(loadedPackage.RuntimeActivationId, backgroundService.CommittedGeneration?.ActivationId);
        }
        finally
        {
            allowStart.TrySetResult();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PreparesExactGenerationBeforeActivationAndRpcPublication()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var process = new PublicationObservingProcessParticipant(owner.State, rpcCatalog);
        var session = CreateRpcSession("process.package", process);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);

        try
        {
            await publisher.PublishAsync(
                session,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None);

            Assert.True(process.Prepared);
            Assert.True(process.Activated);
            Assert.Equal(owner.Generation, process.ObservedGeneration);
            Assert.Equal(0, process.ObservedProviderCount);
            Assert.Single(rpcCatalog.GetSnapshot().Providers);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PreparesEveryProcessBeforeActivatingAnyProcess()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var barrier = new ProcessPreparationBarrier(expectedParticipants: 2);
        var firstParticipant = new BarrierObservingProcessParticipant(barrier);
        var secondParticipant = new BarrierObservingProcessParticipant(barrier);
        var packages = new[]
        {
            CreateRpcLoadedPackage("first.process", "first.provider", firstParticipant),
            CreateRpcLoadedPackage("second.process", "second.provider", secondParticipant),
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => CreateSessionPackage(package.Descriptor.PackageId, isEnabled: true),
                StringComparer.OrdinalIgnoreCase),
            workersStarted: false);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);

        try
        {
            await publisher.PublishAsync(session, null, [], [], owner.Generation, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, barrier.PreparedCount);
            Assert.Equal(2, barrier.ActivationStartedCount);
            Assert.True(firstParticipant.Activated);
            Assert.True(secondParticipant.Activated);
            Assert.Equal(2, rpcCatalog.GetSnapshot().Providers.Count);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_ProcessFaultAfterActivationAcknowledgmentIsNotPublished()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var process = new FaultingDuringActivationProcessParticipant();
        var session = CreateRpcSession("process.package", process);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                session,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None));

            Assert.Contains("faulted after activation", exception.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Empty(rpcCatalog.GetSnapshot().Providers);
            using var lease = owner.State.AcquireLease();
            Assert.True(lease.Session.IsEmpty);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionRetirement_CallbackFailureCannotInterruptDrain()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var session = CreateSession("test.package", new TestBackgroundService());
        await owner.PublishAsync(session, owner.Sources.Snapshot(), [], [], owner.Generation);
        using var lease = owner.State.AcquireLease();
        using var callback = lease.RetirementToken.Register(
            static () => throw new InvalidOperationException("retirement callback failed"));
        lease.Dispose();

        await owner.State.ClearActiveSessionAsync();

        Assert.True(owner.State.ActiveSession.IsEmpty);
    }

    [Fact]
    public async Task SessionRetirement_WaitsForCallbacksBeforeStoppingPackageCode()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var service = new RecordingGenerationBackgroundService();
        var session = CreateSession("test.package", service);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        await publisher.PublishAsync(
            session,
            null,
            [],
            [],
            owner.Generation,
            CancellationToken.None);
        using var lease = owner.State.AcquireLease();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = lease.RetirementToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });
        lease.Dispose();

        var retirement = owner.State.ClearActiveSessionAsync();
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Eventually.AssertPendingAsync(retirement, () => $"stopCount={service.StopCount}; callbackReleased={releaseCallback.IsSet}");

        Assert.Equal(0, service.StopCount);
        releaseCallback.Set();
        await retirement;
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task SessionRetirement_AfterAbortedDrainStillWaitsForEarlierCallbacks()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var service = new RecordingGenerationBackgroundService();
        var session = CreateSession("test.package", service);
        var candidate = ActivePackageSession.CreateEmpty();
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        await publisher.PublishAsync(session, null, [], [], owner.Generation, CancellationToken.None);
        var lease = owner.State.AcquireLease();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = lease.RetirementToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });
        using var cancellation = new CancellationTokenSource();

        try
        {
            var prepare = owner.State.PreparePublicationAsync(
                candidate,
                owner.Generation,
                cancellation.Token);
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
            lease.Dispose();

            var retirement = owner.State.ClearActiveSessionAsync();
            await Eventually.AssertPendingAsync(retirement, () => $"stopCount={service.StopCount}; callbackReleased={releaseCallback.IsSet}");

            Assert.Equal(0, service.StopCount);
            releaseCallback.Set();
            await retirement;
            Assert.Equal(1, service.StopCount);
        }
        finally
        {
            releaseCallback.Set();
            lease.Dispose();
            if (!owner.State.ActiveSession.IsEmpty)
            {
                await owner.State.ClearActiveSessionAsync();
            }
            await candidate.DisposeAsync();
        }
    }

    [Fact]
    public async Task PackageFault_WaitsForRpcRetirementCallbacksBeforeStoppingPackageCode()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var service = new FaultingGenerationBackgroundService();
        var session = CreateRpcSession("faulting.package", service);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        await publisher.PublishAsync(session, null, [], [], owner.Generation, CancellationToken.None);
        var loadedPackage = session.LoadedPackageMap["faulting.package"];
        Assert.True(rpcCatalog.TryGetPackage(
            "faulting.package",
            loadedPackage.RuntimeActivationId,
            out var rpcActivation));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = rpcActivation!.RetirementToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });

        try
        {
            service.Fail(new InvalidOperationException("Generation failed."));
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForPackageAsync(
                owner,
                "faulting.package",
                package => package.Readiness == PackageReadinessState.Failed);
            await Eventually.AssertPendingAsync(
                service.Stopped,
                () => $"stopCount={service.StopCount}; callbackReleased={releaseCallback.IsSet}");

            Assert.Equal(0, service.StopCount);
            releaseCallback.Set();
            await Eventually.ObserveAsync(
                _ => ValueTask.FromResult(service.StopCount),
                static count => count == 1,
                TimeSpan.FromSeconds(2),
                describe: (count, error) => $"stopCount={count}; error={error?.Message}");
            Assert.Equal(1, service.StopCount);
        }
        finally
        {
            releaseCallback.Set();
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task GenerationRetirement_ReentrantCallbackDoesNotBlockFaultAndShutdownDrainsOldLease()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var service = new RecordingGenerationBackgroundService();
        var session = CreateSession("faulting.package", service);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        await publisher.PublishAsync(
            session,
            owner.Sources.Snapshot(),
            [],
            [],
            owner.Generation,
            CancellationToken.None);
        var lease = owner.State.AcquireLease();
        var loadedPackage = Assert.IsType<ActiveLoadedPackage>(
            owner.State.GetLoadedPackage(lease, "faulting.package"));
        var identity = lease.GetPackageActivationIdentity(loadedPackage);
        var generation = lease.Generation;
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = lease.GenerationRetirementToken.Register(() =>
        {
            using var freshLease = Task.Run(owner.State.AcquireLease)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
            Assert.Equal(generation + 1, freshLease.Generation);
            Assert.NotSame(lease.GenerationIdentity, freshLease.GenerationIdentity);
            callbackEntered.TrySetResult();
            releaseCallback.Wait();
        });
        Task<bool>? fault = null;
        Task<IReadOnlyList<string>>? shutdown = null;

        try
        {
            fault = Task.Run(() => owner.State.HandlePackageFault(
                "faulting.package",
                identity,
                generation,
                PackageFailureOrigin.RuntimeBackgroundService,
                new InvalidOperationException("fault generation"),
                "run package",
                committed: _ =>
                {
                    Assert.True(lease.GenerationRetirementToken.IsCancellationRequested);
                    return Task.CompletedTask;
                }));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            shutdown = owner.State.ClearActiveSessionAsync();
            await Task.Delay(50);

            Assert.True(await fault.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(shutdown.IsCompleted);
            Assert.True(lease.GenerationRetirementToken.IsCancellationRequested);
            Assert.True(lease.RetirementToken.IsCancellationRequested);
            releaseCallback.Set();
            Assert.False(shutdown.IsCompleted);

            lease.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Throws<ObjectDisposedException>(() =>
                _ = lease.GenerationRetirementToken.WaitHandle);
            Assert.Throws<ObjectDisposedException>(() =>
                _ = lease.RetirementToken.WaitHandle);
        }
        finally
        {
            releaseCallback.Set();
            lease.Dispose();
            if (fault is not null)
            {
                await fault.WaitAsync(TimeSpan.FromSeconds(2));
            }
            if (shutdown is not null)
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else if (!owner.State.ActiveSession.IsEmpty)
            {
                await owner.State.ClearActiveSessionAsync();
            }
        }
    }

    [Fact]
    public Task PackageFaultPublication_ConcurrentClearWaitsForCommittedSnapshotAndEvent()
        => AssertFaultPublicationSurvivesConcurrentClearAsync(runtimeGenerationFault: false);

    [Fact]
    public Task RuntimeGenerationFaultPublication_ConcurrentClearWaitsForCommittedSnapshotAndEvent()
        => AssertFaultPublicationSurvivesConcurrentClearAsync(runtimeGenerationFault: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageFault_StaleExactTargetDoesNotAdvanceGeneration(
        bool runtimeGenerationFault)
    {
        const string packageId = "faulting.package";
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var session = CreateSession(packageId, new RecordingGenerationBackgroundService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var committed = false;

        try
        {
            await publisher.PublishAsync(
                session,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None);
            PackageActivationIdentity staleActivationIdentity;
            RuntimePackageGeneration staleRuntimeGeneration;
            using (var lease = owner.State.AcquireLease())
            {
                var package = Assert.IsType<ActiveLoadedPackage>(
                    owner.State.GetLoadedPackage(lease, packageId));
                staleActivationIdentity = new PackageActivationIdentity(
                    new object(),
                    package.RuntimeActivationId);
                staleRuntimeGeneration = new RuntimePackageGeneration(
                    package.RuntimeActivationId,
                    lease.Generation + 1);
            }

            Task Publish(long _)
            {
                committed = true;
                return Task.CompletedTask;
            }

            var applied = runtimeGenerationFault
                ? owner.State.HandleRuntimeGenerationFault(
                    packageId,
                    staleRuntimeGeneration,
                    expectedGeneration: 1,
                    PackageFailureOrigin.RuntimeBackgroundService,
                    new InvalidOperationException("Stale Runtime generation fault."),
                    "test stale Runtime generation fault",
                    Publish)
                : owner.State.HandlePackageFault(
                    packageId,
                    staleActivationIdentity,
                    expectedGeneration: 1,
                    PackageFailureOrigin.RuntimeRpcProvider,
                    new InvalidOperationException("Stale package activation fault."),
                    "test stale package activation fault",
                    Publish);

            Assert.False(applied);
            Assert.False(committed);
            Assert.Equal(1, owner.Generation);
            Assert.Equal(packageId, Assert.Single(owner.State.GetActivePackages()).PackageId);
            Assert.Equal(
                PackageReadinessState.Ready,
                Assert.Single(owner.State.GetSessionPackages()).Readiness);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PublishesPackagesAndFreshEmptySessionsRepeatedly()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots,
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        var firstPackages = CreateRpcSession("first.package");
        var firstEmpty = ActivePackageSession.CreateEmpty();
        var secondPackages = CreateRpcSession("second.package");
        var secondEmpty = ActivePackageSession.CreateEmpty();

        try
        {
            await publisher.PublishAsync(firstPackages, null, [], [], owner.Generation, CancellationToken.None);
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            Assert.Single(rpcCatalog.GetSnapshot().Providers);

            await publisher.PublishAsync(firstEmpty, null, [], [], owner.Generation, CancellationToken.None);
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            Assert.Empty(rpcCatalog.GetSnapshot().Providers);

            await publisher.PublishAsync(secondPackages, null, [], [], owner.Generation, CancellationToken.None);
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            Assert.Single(rpcCatalog.GetSnapshot().Providers);

            var publication = await publisher.PublishAsync(
                secondEmpty,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None);

            Assert.Equal(4, publication.Stamp.SessionGeneration);
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            Assert.Empty(rpcCatalog.GetSnapshot().Providers);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Same(secondEmpty, owner.State.ActiveSession);
            Assert.NotSame(firstEmpty, secondEmpty);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            await firstPackages.DisposeAsync();
            await firstEmpty.DisposeAsync();
            await secondPackages.DisposeAsync();
            await secondEmpty.DisposeAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_GenerationActivationFailureKeepsPublishedSessionAndProviders()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots,
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            new RuntimeLifecyclePolicyOptions
            {
                PackageRuntimeGenerationActivationAttempts = 1,
            },
            rpcCatalog);
        var activeSession = CreateRpcSession("active.package");
        var failingSession = CreateSession(
            "failing.package",
            new RepeatedlyFailingGenerationBackgroundService());

        try
        {
            await publisher.PublishAsync(activeSession, null, [], [], owner.Generation, CancellationToken.None);
            var staleEndpoint = Assert.Single(rpcCatalog.GetSnapshot().Providers).Endpoint;

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                failingSession,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None));

            Assert.Equal(1, owner.Generation);
            Assert.Same(activeSession, owner.State.ActiveSession);
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            Assert.Single(rpcCatalog.GetSnapshot().Providers);
            Assert.True(rpcCatalog.TryGetActiveEndpoint(staleEndpoint, out _, out var stale));
            Assert.False(stale);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            await activeSession.DisposeAsync();
            await failingSession.DisposeAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task LoadInstalledPackagesAsync_EmptyNoOpRepairsStaleRpcCatalog()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var service = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer);
        var staleSession = CreateRpcSession("stale.package");

        try
        {
            service.RpcCatalog.ActivateSession(staleSession, service.SessionGeneration);
            Assert.Single(service.RpcCatalog.GetSnapshot().Providers);

            var result = await service.LoadInstalledPackagesAsync();

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(0, service.SessionGeneration);
            Assert.True(service.ActiveSession.IsEmpty);
            Assert.Equal(service.SessionGeneration, service.RpcCatalog.SessionGeneration);
            Assert.Empty(service.RpcCatalog.GetSnapshot().Providers);
        }
        finally
        {
            await service.ShutdownAsync();
            await staleSession.DisposeAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutDoesNotPublishAndStopsAttemptedService()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMilliseconds(50),
            PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(200),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new HangingStartBackgroundService();
        var session = CreateSession("test.package", backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<TimeoutException>(() => publisher.CommitAsync(pending));

            Assert.Contains("did not start", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            using var lease = owner.State.AcquireLease();
            Assert.Equal(0, lease.Generation);
        }
        finally
        {
            backgroundService.Release();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task StartWorkersAsync_AppliesStartupTimeoutToEachPackageWorker()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestBackgroundService(startEntered: firstEntered, allowStart: firstRelease.Task);
        var second = new TestBackgroundService(startEntered: secondEntered, allowStart: secondRelease.Task);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateLoadedPackage("first.package", first),
                ["second.package"] = CreateLoadedPackage("second.package", second),
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateSessionPackage("first.package", isEnabled: true),
                ["second.package"] = CreateSessionPackage("second.package", isEnabled: true),
            },
            workersStarted: false);

        try
        {
            var startup = session.StartWorkersAsync(
                NullLogger.Instance,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(300);
            firstRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(300);
            secondRelease.TrySetResult();

            await startup;

            Assert.Equal(1, first.StartCount);
            Assert.Equal(1, second.StartCount);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
            await session.StopWorkersAsync(NullLogger.Instance, TimeSpan.FromSeconds(1));
            await session.DisposeAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task StartWorkersAsync_UsesBoundedDependencyWaves()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWaveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestBackgroundService(startEntered: firstEntered, allowStart: firstWaveRelease.Task);
        var second = new TestBackgroundService(startEntered: secondEntered, allowStart: firstWaveRelease.Task);
        var third = new TestBackgroundService(startEntered: thirdEntered, allowStart: firstWaveRelease.Task);
        var dependent = new TestBackgroundService(startEntered: dependentEntered, allowStart: dependentRelease.Task);
        var packages = new[]
        {
            CreateLoadedPackage("first.package", first),
            CreateLoadedPackage("second.package", second),
            CreateLoadedPackage("third.package", third),
            CreateLoadedPackage("dependent.package", dependent),
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => CreateSessionPackage(package.Descriptor.PackageId, isEnabled: true),
                StringComparer.OrdinalIgnoreCase),
            workersStarted: false,
            workerStartupWaves:
            [
                ["first.package", "second.package", "third.package"],
                ["dependent.package"],
            ],
            maximumWorkerStartupConcurrency: 2);

        try
        {
            var startup = session.StartWorkersAsync(
                NullLogger.Instance,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1));
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, third.StartCount);
            Assert.Equal(0, dependent.StartCount);

            firstWaveRelease.TrySetResult();
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await dependentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, third.StartCount);
            Assert.Equal(1, dependent.StartCount);
            dependentRelease.TrySetResult();
            await startup;
        }
        finally
        {
            firstWaveRelease.TrySetResult();
            dependentRelease.TrySetResult();
            await session.StopWorkersAsync(NullLogger.Instance, TimeSpan.FromSeconds(1));
            await session.DisposeAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task StartWorkersAsync_WaveFailureCancelsSiblingAndDoesNotStartDependents()
    {
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = new TestBackgroundService(startEntered: siblingEntered, allowStart: neverRelease.Task);
        var failing = new FailingAfterSignalBackgroundService(siblingEntered.Task);
        var dependent = new TestBackgroundService();
        var packages = new[]
        {
            CreateLoadedPackage("sibling.package", sibling),
            CreateLoadedPackage("failing.package", failing),
            CreateLoadedPackage("dependent.package", dependent),
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => CreateSessionPackage(package.Descriptor.PackageId, isEnabled: true),
                StringComparer.OrdinalIgnoreCase),
            workersStarted: false,
            workerStartupWaves:
            [
                ["sibling.package", "failing.package"],
                ["dependent.package"],
            ],
            maximumWorkerStartupConcurrency: 2);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartWorkersAsync(
                NullLogger.Instance,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1)));

            Assert.Equal("injected wave failure", exception.Message);
            Assert.Equal(0, dependent.StartCount);
            Assert.Equal(1, sibling.StopCount);
            Assert.Equal(1, failing.StopCount);
        }
        finally
        {
            neverRelease.TrySetResult();
            await session.DisposeAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_CandidateStartFailureLeavesCommittedGenerationActive()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots,
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        var activeService = new RecordingGenerationBackgroundService();
        var activeSession = CreateRpcSession("active.package", activeService);
        var failingService = new ImmediateFailingGenerationBackgroundService();
        var candidateSession = CreateSession("candidate.package", failingService);

        try
        {
            var initial = publisher.Prepare(activeSession, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var initialPending = await publisher.BeginPublishAsync(initial, CancellationToken.None);
            await publisher.CommitAsync(initialPending);
            var catalogBeforeFailure = rpcCatalog.GetSnapshot();
            var endpointBeforeFailure = Assert.Single(catalogBeforeFailure.Providers).Endpoint;

            var candidate = publisher.Prepare(candidateSession, owner.Sources.Snapshot(), [], [], baseGeneration: 1);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.CommitAsync(pending));

            Assert.Equal(1, owner.Generation);
            Assert.Equal(1, activeService.StartCount);
            Assert.Equal(1, activeService.CommitCount);
            Assert.Equal(0, activeService.StopCount);
            Assert.Equal(1, failingService.StartCount);
            Assert.Equal(0, failingService.CommitCount);
            Assert.Equal(1, failingService.StopCount);
            var catalogAfterFailure = rpcCatalog.GetSnapshot();
            Assert.Equal(catalogBeforeFailure.Revision, catalogAfterFailure.Revision);
            Assert.Equal(endpointBeforeFailure, Assert.Single(catalogAfterFailure.Providers).Endpoint);
            using var lease = owner.State.AcquireLease();
            Assert.Same(activeSession, lease.Session);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PrecommitCandidateStartFailureRestoresOldSessionAndRpcCatalog()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        var activeService = new RecordingGenerationBackgroundService();
        var activeSession = CreateRpcSession("active.package", activeService);
        var failingService = new ImmediateFailingGenerationBackgroundService();
        var candidateSession = CreateSession("candidate.package", failingService);

        try
        {
            await publisher.PublishAsync(
                activeSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            var catalogBeforeFailure = rpcCatalog.GetSnapshot();
            var endpointBeforeFailure = Assert.Single(catalogBeforeFailure.Providers).Endpoint;
            var candidate = publisher.Prepare(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                baseGeneration: 1,
                requiresPackageStoreCommit: true);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.StartCandidateAsync(pending));
            await publisher.DiscardAsync(pending);

            Assert.Equal(1, owner.Generation);
            Assert.Same(activeSession, owner.State.ActiveSession);
            Assert.Equal(0, activeService.StopCount);
            Assert.Equal(1, failingService.StartCount);
            Assert.Equal(1, failingService.StopCount);
            Assert.Equal(catalogBeforeFailure.Revision, rpcCatalog.Revision);
            Assert.True(rpcCatalog.TryGetActiveEndpoint(endpointBeforeFailure, out _, out var stale));
            Assert.False(stale);
            using var lease = owner.State.AcquireLease();
            Assert.Same(activeSession, lease.Session);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PrecommitCandidateStartIsIdempotentAndHidden()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        var activeSession = CreateRpcSession("active.package");
        var candidateService = new RecordingGenerationBackgroundService();
        var candidateSession = CreateRpcSession("candidate.package", candidateService);

        try
        {
            await publisher.PublishAsync(
                activeSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            var publicCatalog = rpcCatalog.GetSnapshot();
            var candidate = publisher.Prepare(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                baseGeneration: 1,
                requiresPackageStoreCommit: true);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var firstStart = publisher.StartCandidateAsync(pending);
            var secondStart = publisher.StartCandidateAsync(pending);
            Assert.Same(firstStart, secondStart);
            await Task.WhenAll(firstStart, secondStart);

            Assert.Equal(1, candidateService.StartCount);
            Assert.Equal(0, candidateService.CommitCount);
            Assert.Equal(1, owner.Generation);
            Assert.Equal(publicCatalog.Revision, rpcCatalog.Revision);
            Assert.Equal(
                "example.provider",
                Assert.Single(rpcCatalog.GetSnapshot().Providers).ProviderId);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            await publisher.DiscardAsync(pending);
            using var lease = owner.State.AcquireLease();
            Assert.Same(activeSession, lease.Session);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_PostDurableCandidateFailurePublishesUnavailableWithoutRestoringOldBinding()
    {
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            new RuntimeLifecyclePolicyOptions
            {
                PackageRuntimeGenerationActivationAttempts = 1,
            },
            rpcCatalog);
        var activeService = new RecordingGenerationBackgroundService();
        var activeSession = CreateRpcSession("active.package", activeService);
        var failingService = new RepeatedlyFailingGenerationBackgroundService();
        var candidateSession = CreateSession("candidate.package", failingService);

        try
        {
            await publisher.PublishAsync(
                activeSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            var oldEndpoint = Assert.Single(rpcCatalog.GetSnapshot().Providers).Endpoint;
            var candidate = publisher.Prepare(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                baseGeneration: 1,
                requiresPackageStoreCommit: true);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            await publisher.StartCandidateAsync(pending);
            publisher.MarkPackageStoreCommitted(pending);

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.CommitAsync(pending));

            Assert.Equal(2, owner.Generation);
            Assert.NotSame(activeSession, owner.State.ActiveSession);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            Assert.Equal(1, activeService.StopCount);
            Assert.Equal(1, failingService.StopCount);
            Assert.False(rpcCatalog.TryGetActiveEndpoint(oldEndpoint, out _, out var stale));
            Assert.True(stale);
            Assert.Empty(rpcCatalog.GetSnapshot().Providers);
            Assert.Contains(
                owner.GetSnapshot().Errors,
                error => error.Contains("package store became authoritative", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_ReopensPreviousSessionOnlyAfterCandidateCleanup()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var activeSession = CreateSession("active.package", new RecordingGenerationBackgroundService());
        var failingWorker = new FailingCommitWithBlockingStopWorker();
        var candidateSession = CreateSession("candidate.package", failingWorker);

        try
        {
            await publisher.PublishAsync(
                activeSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            var rejected = publisher.PublishAsync(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 1,
                CancellationToken.None);
            await failingWorker.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, owner.Generation);
            Assert.Equal("active.package", Assert.Single(owner.GetSnapshot().ActivePackages).PackageId);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            failingWorker.ReleaseStop();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);

            using var lease = owner.State.AcquireLease();
            Assert.Same(activeSession, lease.Session);
            Assert.Equal(1, lease.Generation);
        }
        finally
        {
            failingWorker.ReleaseStop();
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_RuntimeGenerationFaultDisablesExactActivePackage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var publisher = new PackageSessionPublisher(owner, NullLogger<PackageSessionPublisher>.Instance);
        var service = new FaultingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var publication = await publisher.CommitAsync(pending);

            service.Fail(new InvalidOperationException("Generation lease was stolen."));
            var failed = await WaitForPackageAsync(
                owner,
                "test.package",
                package => package.Readiness == PackageReadinessState.Failed);

            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, failed.FailureOrigin);
            Assert.Contains("Generation lease was stolen.", failed.LastError, StringComparison.Ordinal);
            Assert.Equal(publication.Stamp.SessionGeneration + 1, owner.Generation);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ExecutionRuntimePolicyFault_ExposesWorkerCleanupProof()
    {
        const string packageId = "test.package";
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var worker = new BlockingPolicyCleanupWorker();
        var loaded = CreateLoadedPackage(packageId, worker);
        loaded = loaded with
        {
            ExecutionRuntimeBinding = new RuntimePackageExecutionRuntimeBinding(
                SunderPackageFormat.RuntimeHostRole,
                "test-rid",
                "test-runtime",
                "1.0.0",
                1,
                "test-abi",
                new string('a', 64),
                new string('b', 64),
                1,
                "https://registry.example.test",
                RuntimeExecutionRuntimeAcquisitionSources.Registry,
                "test-key",
                1,
                1,
                Sunder.Registry.Contracts.ExecutionRuntimeLifecycleStates.Supported,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loaded,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = CreateSessionPackage(packageId, isEnabled: true),
            },
            workersStarted: false);

        try
        {
            await publisher.PublishAsync(
                session,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            Assert.Equal(
                [packageId],
                owner.State.GetExecutionRuntimeDependentPackageIds(
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        loaded.ExecutionRuntimeBinding!.Identity.SingleFlightKey,
                    }));
            Assert.True(owner.EnforceExecutionRuntimePolicy(
                packageId,
                "Execution runtime repair is pending.",
                out var cleanupCompletion));
            await worker.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(cleanupCompletion.IsCompleted);
            Assert.Equal(
                PackageReadinessState.Failed,
                Assert.Single(owner.GetSnapshot().SessionPackages).Readiness);

            worker.ReleaseStop();
            await cleanupCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            worker.ReleaseStop();
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task ExecutionRuntimeRepair_HoldsOperationGateThroughStoreRepair()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        await using var gate = new RuntimeOperationGate();
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            operationGate: gate);
        var repairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRepair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ExecutionRuntimeIdentity(
            "test-runtime",
            "1.0.0",
            1,
            "test-rid",
            "test-abi");

        try
        {
            var repair = host.ExecuteExecutionRuntimeRepairAsync(
                identity,
                _ => Task.FromResult(new ExecutionRuntimeVerificationResult(
                    Healthy: false,
                    DeepVerified: true,
                    DateTimeOffset.UtcNow,
                    "Corrupted.")),
                async token =>
                {
                    repairEntered.TrySetResult();
                    await releaseRepair.Task.WaitAsync(token);
                    return null;
                });
            await repairEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var competingOperation = gate.EnterAsync().AsTask();
            Assert.False(competingOperation.IsCompleted);

            releaseRepair.TrySetResult();
            var result = await repair.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Verification.Healthy);
            Assert.Null(result.Repaired);
            Assert.Null(result.Reload);

            var competingLease = await competingOperation.WaitAsync(TimeSpan.FromSeconds(2));
            await competingLease.DisposeAsync();
        }
        finally
        {
            releaseRepair.TrySetResult();
            await host.ShutdownAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ExecutionRuntimeRecovery_DrainsDependentWorkersBeforeRecoveryMutation()
    {
        const string packageId = "recovery.package";
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        var installedPackages = new InstalledPackageStore(paths);
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            installedPackages,
            new SunderPackageArchiveInstaller(paths));
        var worker = new BlockingPolicyCleanupWorker();
        using var cancellation = new CancellationTokenSource();
        Task? recovery = null;

        try
        {
            await host.InitializeAsync();
            var packageRoot = paths.GetInstalledPackagePath(packageId, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                packageRoot,
                packageId,
                "1.0.0",
                typeof(ActivePackageSessionTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()]);
            var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
                packageRoot,
                packageId,
                "1.0.0",
                isEnabled: false);
            await installedPackages.WriteAsync([installed]);
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            var runtimeBinding = RuntimePackageExecutionRuntimeBinding.FromInstalled(binding);
            var sessionFolder = Path.Combine(rootPath, "active-session");
            var loaded = CreateLoadedPackageCore(
                packageId,
                sessionFolder,
                disposalWorker: null,
                workers: [worker]) with
            {
                ExecutionRuntimeBinding = runtimeBinding,
            };
            var session = new ActivePackageSession(
                sessionFolder,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = loaded,
                },
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = CreateSessionPackage(packageId, isEnabled: true),
                },
                workersStarted: false);
            var publisher = new PackageSessionPublisher(
                host.SessionOwner,
                NullLogger<PackageSessionPublisher>.Instance);
            await publisher.PublishAsync(
                session,
                host.SessionOwner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: host.SessionOwner.Generation,
                CancellationToken.None);
            var impact = await host.GetExecutionRuntimeImpactAsync(runtimeBinding.Identity);
            Assert.Equal([packageId], impact.ImpactedPackageIds);
            var recoveryEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            recovery = host.ExecuteExecutionRuntimeRecoveryAsync(
                runtimeBinding.Identity,
                impact.Fingerprint,
                async token =>
                {
                    recoveryEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("The blocked recovery unexpectedly resumed.");
                },
                cancellation.Token);
            await worker.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(recoveryEntered.Task.IsCompleted);

            worker.ReleaseStop();
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                PackageReadinessState.Failed,
                Assert.Single(host.SessionOwner.GetSnapshot().SessionPackages).Readiness);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery!);
        }
        finally
        {
            cancellation.Cancel();
            worker.ReleaseStop();
            if (recovery is not null)
            {
                try
                {
                    await recovery;
                }
                catch
                {
                }
            }
            await host.ShutdownAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_SiblingFaultPreservesSurvivorPackageDataAuthority()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var faultingWorker = new FaultingGenerationBackgroundService();
        var survivorWorker = new PackageDataAuthorityWorker(owner.State, "survivor.package");
        var faultingPackage = CreateLoadedPackage("faulting.package", faultingWorker);
        var survivorPackage = CreateLoadedPackage("survivor.package", survivorWorker);
        var packages = new[] { faultingPackage, survivorPackage };
        var session = new ActivePackageSession(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => CreateSessionPackage(package.Descriptor.PackageId, isEnabled: true),
                StringComparer.OrdinalIgnoreCase),
            workersStarted: false);

        try
        {
            var publication = await publisher.PublishAsync(
                session,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            Assert.Equal(1, publication.Stamp.SessionGeneration);

            faultingWorker.Fail(new InvalidOperationException("Sibling generation failed."));
            await WaitForPackageAsync(
                owner,
                "faulting.package",
                package => package.Readiness == PackageReadinessState.Failed);
            Assert.Equal(2, owner.Generation);

            var values = await survivorWorker.RoundTripAsync();
            Assert.Equal("surviving-state", values.State);
            Assert.Equal("surviving-secret", values.Secret);

            var data = new PackageDataAccessService(owner.State);
            Assert.True(await data.SetStateAsync(
                "survivor.package",
                "public.state",
                "public-value",
                CancellationToken.None));
            Assert.Equal(
                "public-value",
                (await data.GetStateAsync(
                    "survivor.package",
                    "public.state",
                    CancellationToken.None))?.Value);

            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquirePackageActivationLease(
                "survivor.package",
                survivorWorker.Generation! with { SessionGeneration = owner.Generation }));
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquirePackageActivationLease(
                "survivor.package",
                survivorWorker.Generation! with { ActivationId = Guid.NewGuid() }));
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_ActivationFailureRollsBackStateSettingsSecretsAndJournalsAcrossPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-migration-transaction-tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "package-data");
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var firstContext = new RuntimePackageDataResources("first.package", "1.0.0", dataRoot);
        var secondContext = new RuntimePackageDataResources("second.package", "1.0.0", dataRoot);
        var statePlan = new PackageStorageMigrationPlanIdentity(
            "state",
            "state:1.0.0",
            new string('a', 64));
        var secretPlan = new PackageStorageMigrationPlanIdentity(
            "secrets",
            "secrets:1.0.0",
            new string('b', 64));

        try
        {
            await SeedLegacyStorageAsync(firstContext, "first");
            await SeedLegacyStorageAsync(secondContext, "second");
            var failedSession = CreateStorageMigrationSession(
                new TransactionalStorageMigrationWorker(firstContext, [statePlan, secretPlan], failActivation: false),
                new TransactionalStorageMigrationWorker(secondContext, [statePlan, secretPlan], failActivation: true),
                firstContext,
                secondContext);

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                failedSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None));

            Assert.Equal(0, owner.Generation);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            await AssertLegacyStorageAsync(firstContext, "first");
            await AssertLegacyStorageAsync(secondContext, "second");
            Assert.False(await firstContext.MigrationJournal.IsCompletedAsync(statePlan));
            Assert.False(await firstContext.MigrationJournal.IsCompletedAsync(secretPlan));
            Assert.False(await secondContext.MigrationJournal.IsCompletedAsync(statePlan));
            Assert.False(await secondContext.MigrationJournal.IsCompletedAsync(secretPlan));

            var firstRetry = new RuntimePackageDataResources("first.package", "1.0.0", dataRoot);
            var secondRetry = new RuntimePackageDataResources("second.package", "1.0.0", dataRoot);
            var retrySession = CreateStorageMigrationSession(
                new TransactionalStorageMigrationWorker(firstRetry, [statePlan, secretPlan], failActivation: false),
                new TransactionalStorageMigrationWorker(secondRetry, [statePlan, secretPlan], failActivation: false),
                firstRetry,
                secondRetry);
            var publication = await publisher.PublishAsync(
                retrySession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            Assert.Equal(1, publication.Stamp.SessionGeneration);
            await AssertMigratedStorageAsync(firstRetry, "first");
            await AssertMigratedStorageAsync(secondRetry, "second");
            Assert.True(await firstRetry.MigrationJournal.IsCompletedAsync(statePlan));
            Assert.True(await firstRetry.MigrationJournal.IsCompletedAsync(secretPlan));
            Assert.True(await secondRetry.MigrationJournal.IsCompletedAsync(statePlan));
            Assert.True(await secondRetry.MigrationJournal.IsCompletedAsync(secretPlan));
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            firstContext.Dispose();
            secondContext.Dispose();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageStorageActivationTransaction_FileSnapshotFailureRestoresWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-storage-snapshot-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "package-data", "workspace");
        Directory.CreateDirectory(workspace);
        var baselinePath = Path.Combine(workspace, "baseline.txt");
        await File.WriteAllTextAsync(baselinePath, "stable");
        var package = CreateWorkspacePackage(
                root,
                "1.0.0",
                new WorkspaceProbeWorker(new WorkspaceActivationProbe(workspace), candidate: true))
            with
            {
                FileStore = new UnsupportedPackageFileStore(),
            };
        try
        {
            await package.SecretsStore.SetSecretAsync("preserved.secret", "preserved-value");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PackageStorageActivationTransaction.BeginAsync([package], CancellationToken.None));

            Assert.Equal("stable", await File.ReadAllTextAsync(baselinePath));
            Assert.Equal(
                "preserved-value",
                await package.SecretsStore.GetSecretAsync("preserved.secret"));
            Assert.Empty(WorkspaceActivationBackups(workspace));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_SharedWorkspaceFailureRestoresWorkspaceAndRebuildsPreviousGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-workspace-recovery-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "package-data", "workspace");
        Directory.CreateDirectory(workspace);
        var baselinePath = Path.Combine(workspace, "baseline.txt");
        await File.WriteAllTextAsync(baselinePath, "stable");
        var probe = new WorkspaceActivationProbe(workspace);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);

        ActiveLoadedPackage CreatePreviousPackage()
            => CreateWorkspacePackage(
                root,
                "1.0.0",
                new WorkspaceProbeWorker(probe, candidate: false, sessions: owner.State)) with
            {
                RecoveryFactory = CreatePreviousPackage,
            };

        var previousPackage = CreatePreviousPackage();
        var previousActivationId = previousPackage.RuntimeActivationId;
        var previousSession = CreateWorkspaceSession(previousPackage);
        var candidateSession = CreateWorkspaceSession(CreateWorkspacePackage(
            root,
            "2.0.0",
            new WorkspaceProbeWorker(probe, candidate: true)));
        try
        {
            await publisher.PublishAsync(
                previousSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 1,
                CancellationToken.None));

            Assert.False(probe.WorkersOverlapped);
            Assert.Equal(2, probe.PreviousStartCount);
            Assert.Equal(1, probe.PreviousStopCount);
            Assert.Equal(1, probe.CandidateStartCount);
            Assert.Equal(1, probe.CandidateStopCount);
            Assert.Equal(2, probe.RecoveredLeaseGeneration);
            Assert.Equal("stable", await File.ReadAllTextAsync(baselinePath));
            Assert.False(File.Exists(Path.Combine(workspace, "candidate.txt")));
            Assert.Empty(WorkspaceActivationBackups(workspace));
            Assert.Equal(2, owner.Generation);
            Assert.Equal("workspace.package", Assert.Single(owner.GetSnapshot().ActivePackages).PackageId);
            using var lease = owner.State.AcquireLease();
            Assert.Same(previousSession, lease.Session);
            Assert.Equal(2, lease.Generation);
            var recoveredPackage = Assert.IsType<ActiveLoadedPackage>(
                owner.State.GetLoadedPackage(lease, "workspace.package"));
            Assert.NotEqual(previousActivationId, recoveredPackage.RuntimeActivationId);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_SharedWorkspaceRollbackWaitsForPostStartCandidateExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-workspace-recovery-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "package-data", "workspace");
        Directory.CreateDirectory(workspace);
        var baselinePath = Path.Combine(workspace, "baseline.txt");
        await File.WriteAllTextAsync(baselinePath, "stable");
        var probe = new WorkspaceActivationProbe(workspace)
        {
            BlockCandidateStop = true,
        };
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(40),
            });

        ActiveLoadedPackage CreatePreviousPackage()
            => CreateWorkspacePackage(
                root,
                "1.0.0",
                new WorkspaceProbeWorker(probe, candidate: false, sessions: owner.State)) with
            {
                RecoveryFactory = CreatePreviousPackage,
            };

        var previousSession = CreateWorkspaceSession(CreatePreviousPackage());
        var candidateSession = CreateWorkspaceSession(CreateWorkspacePackage(
            root,
            "2.0.0",
            new WorkspaceProbeWorker(
                probe,
                candidate: true,
                failOnStart: false,
                failOnCommit: true)));
        try
        {
            await publisher.PublishAsync(
                previousSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            var rejected = publisher.PublishAsync(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 1,
                CancellationToken.None);
            await probe.CandidateStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            Assert.False(rejected.IsCompleted);
            Assert.Equal("stable", await File.ReadAllTextAsync(baselinePath));
            Assert.True(File.Exists(Path.Combine(workspace, "candidate.txt")));
            Assert.Equal(1, probe.PreviousStartCount);

            probe.ReleaseCandidateStop();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);

            Assert.False(probe.WorkersOverlapped);
            Assert.Equal(2, probe.PreviousStartCount);
            Assert.Equal(1, probe.PreviousStopCount);
            Assert.Equal(1, probe.CandidateStopCount);
            Assert.Equal("stable", await File.ReadAllTextAsync(baselinePath));
            Assert.False(File.Exists(Path.Combine(workspace, "candidate.txt")));
            Assert.Empty(WorkspaceActivationBackups(workspace));
            using var lease = owner.State.AcquireLease();
            Assert.Same(previousSession, lease.Session);
            Assert.Equal(2, lease.Generation);
        }
        finally
        {
            probe.ReleaseCandidateStop();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_SharedWorkspaceRecoveryFailurePublishesUnavailableAndRetiresWorkers()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-workspace-recovery-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "package-data", "workspace");
        Directory.CreateDirectory(workspace);
        var baselinePath = Path.Combine(workspace, "baseline.txt");
        await File.WriteAllTextAsync(baselinePath, "stable");
        var probe = new WorkspaceActivationProbe(workspace)
        {
            FailRecoveredPreviousStart = true,
        };
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);

        ActiveLoadedPackage CreatePreviousPackage()
            => CreateWorkspacePackage(
                root,
                "1.0.0",
                new WorkspaceProbeWorker(probe, candidate: false, sessions: owner.State)) with
            {
                RecoveryFactory = CreatePreviousPackage,
            };

        var previousSession = CreateWorkspaceSession(CreatePreviousPackage());
        var candidateSession = CreateWorkspaceSession(CreateWorkspacePackage(
            root,
            "2.0.0",
            new WorkspaceProbeWorker(probe, candidate: true)));
        try
        {
            await publisher.PublishAsync(
                previousSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);

            await Assert.ThrowsAsync<AggregateException>(() => publisher.PublishAsync(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 1,
                CancellationToken.None));

            Assert.False(probe.WorkersOverlapped);
            Assert.Equal(2, probe.PreviousStartCount);
            Assert.Equal(2, probe.PreviousStopCount);
            Assert.Equal(2, probe.PreviousDisposeCount);
            Assert.Equal(1, probe.CandidateDisposeCount);
            Assert.Equal("stable", await File.ReadAllTextAsync(baselinePath));
            Assert.False(File.Exists(Path.Combine(workspace, "candidate.txt")));
            Assert.Empty(WorkspaceActivationBackups(workspace));
            Assert.Equal(2, owner.Generation);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            Assert.Contains(
                owner.GetSnapshot().Errors,
                error => error.Contains("recovery failed", StringComparison.OrdinalIgnoreCase));
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DelayedFaultCommit_CompletesBeforeNewerSnapshotEventsSourcesOrUi()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var initialFolder = Path.Combine(rootPath, "initial-session");
        var replacementFolder = Path.Combine(rootPath, "replacement-session");
        Directory.CreateDirectory(initialFolder);
        Directory.CreateDirectory(replacementFolder);
        var paths = new RuntimePackagePaths(rootPath);
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var initialSession = CreateSession(
            "fault.package",
            initialFolder,
            disposalWorker: null,
            workersStarted: true);
        var replacementSession = CreateSession(
            "replacement.package",
            replacementFolder,
            disposalWorker: null,
            workersStarted: true);
        var initialSource = Assert.Single(initialSession.GetActivePackageSources());
        var replacementSource = Assert.Single(replacementSession.GetActivePackageSources());
        var initialSources = new PackageSessionSourceState().Snapshot();
        initialSources.SetDevOverlay(new PackageSessionDevOverlay(
            "fault.package",
            initialSource.SourceFolder,
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var replacementSources = new PackageSessionSourceState().Snapshot();
        replacementSources.SetDevOverlay(new PackageSessionDevOverlay(
            "replacement.package",
            replacementSource.SourceFolder,
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var faultCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFaultCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? faultTask = null;
        Task<IReadOnlyList<string>>? replacementPublication = null;
        bool? faultCommitApplied = null;
        SessionPackageDescriptor[] faultedPackages = [];
        PackageSessionState.SessionPublicationSnapshot? faultPublicationSnapshot = null;

        try
        {
            await owner.PublishAsync(
                initialSession,
                initialSources,
                [],
                [],
                expectedGeneration: 0);
            PackageActivationIdentity activationIdentity;
            using (var lease = owner.State.AcquireLease())
            {
                var loadedPackage = Assert.IsType<ActiveLoadedPackage>(
                    owner.State.GetLoadedPackage(lease, "fault.package"));
                activationIdentity = lease.GetPackageActivationIdentity(loadedPackage);
            }

            faultTask = Task.Run(() => owner.State.HandlePackageFault(
                "fault.package",
                activationIdentity,
                expectedGeneration: 1,
                PackageFailureOrigin.RuntimeAuthentication,
                new InvalidOperationException("Exact activation fault."),
                "test delayed fault publication",
                committed: generation =>
                {
                    var publicationSnapshot = owner.State.CapturePublicationSnapshot(generation);
                    faultPublicationSnapshot = publicationSnapshot;
                    faultedPackages = publicationSnapshot.SessionPackages.ToArray();
                    faultCommitEntered.TrySetResult();
                    releaseFaultCommit.Task.GetAwaiter().GetResult();
                    faultCommitApplied = owner.CommitGeneration(
                        publicationSnapshot.Generation,
                        publicationSnapshot.ActivePackages,
                        publicationSnapshot.SessionPackages,
                        initialSources,
                        [],
                        ["Package 'fault.package' failed: Exact activation fault."]);
                    return Task.CompletedTask;
                }));
            await faultCommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var exactFault = Assert.Single(faultedPackages);
            Assert.Equal(PackageReadinessState.Failed, exactFault.Readiness);
            Assert.Equal(PackageFailureOrigin.RuntimeAuthentication, exactFault.FailureOrigin);
            Assert.Equal("Exact activation fault.", exactFault.LastError);
            Assert.Equal(2, owner.Generation);

            replacementPublication = owner.PublishAsync(
                replacementSession,
                replacementSources,
                ["newer warning"],
                ["newer error"],
                expectedGeneration: 2);
            Assert.False(replacementPublication.IsCompleted);
            Assert.Equal(1, owner.GetSnapshot().SessionGeneration);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            releaseFaultCommit.TrySetResult();
            Assert.True(await faultTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await replacementPublication.WaitAsync(TimeSpan.FromSeconds(5));

            var newerSnapshot = owner.GetSnapshot();
            Assert.Equal(3, owner.Generation);
            Assert.Equal("replacement.package", Assert.Single(owner.State.GetActivePackages()).PackageId);
            Assert.Equal(3, newerSnapshot.SessionGeneration);
            Assert.Equal("replacement.package", Assert.Single(newerSnapshot.ActivePackages).PackageId);
            Assert.Equal("replacement.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);
            Assert.Contains("newer warning", newerSnapshot.Warnings);
            Assert.Contains("newer error", newerSnapshot.Errors);
            Assert.True(faultCommitApplied);
            var capturedFaultPublication = Assert.IsType<PackageSessionState.SessionPublicationSnapshot>(
                faultPublicationSnapshot);
            Assert.Equal(2, capturedFaultPublication.Generation);
            Assert.Empty(capturedFaultPublication.ActivePackages);
            Assert.Equal(
                PackageReadinessState.Failed,
                Assert.Single(capturedFaultPublication.SessionPackages).Readiness);
            Assert.Equal(3, events.GetSnapshot().SessionGeneration);
            var publishedGenerations = events.GetSnapshot().Events
                .Where(runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged)
                .Select(runtimeEvent => runtimeEvent.SessionGeneration)
                .ToArray();
            Assert.Equal([1L, 2L, 3L], publishedGenerations);
            Assert.Equal(publishedGenerations.Order(), publishedGenerations);
            Assert.Equal("replacement.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);
        }
        finally
        {
            releaseFaultCommit.TrySetResult();
            if (faultTask is not null)
            {
                await faultTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (replacementPublication is not null)
            {
                await replacementPublication.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_BlockedGenerationActivationNeverBecomesPublicOrLeasable()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                SessionDrainTimeout = TimeSpan.FromMilliseconds(50),
            },
            uiSnapshots: snapshots);
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMilliseconds(50),
            PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var service = new BlockingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var commit = publisher.CommitAsync(pending);
            await service.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(0, owner.GetSnapshot().SessionGeneration);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            await Assert.ThrowsAsync<TimeoutException>(() => commit.WaitAsync(TimeSpan.FromSeconds(2)));
            await service.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            Assert.Equal(1, service.CommitCount);
            Assert.Equal(0, service.StopCount);

            service.ReleaseCommit();
            await Assert.ThrowsAsync<TimeoutException>(() => commit);
            await service.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (var lease = owner.State.AcquireLease())
            {
                Assert.True(lease.Session.IsEmpty);
                Assert.Equal(0, lease.Generation);
            }
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, service.StopCount);
        }
        finally
        {
            service.ReleaseCommit();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_MissingActivationAcknowledgementUsesLifecycleDeadline()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMilliseconds(500),
            PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var service = new BlockingActivationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var commit = publisher.CommitAsync(pending);
            await service.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => commit);

            Assert.Contains(
                "commit, migration preparation, and activation acknowledgement",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, owner.Generation);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            using var lease = owner.State.AcquireLease();
            Assert.True(lease.Session.IsEmpty);
            Assert.Equal(0, lease.Generation);
        }
        finally
        {
            service.ReleaseActivation();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_BlockedGenerationActivationBecomesLeasableAfterExactCommit()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var publisher = new PackageSessionPublisher(owner, NullLogger<PackageSessionPublisher>.Instance);
        var service = new BlockingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var commit = publisher.CommitAsync(pending);
            await service.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unpublished = owner.GetSnapshot();
            Assert.Equal(0, unpublished.SessionGeneration);
            Assert.Empty(unpublished.ActivePackages);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            service.ReleaseCommit();
            var result = await commit.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, result.Stamp.SessionGeneration);
            var published = owner.GetSnapshot();
            Assert.Equal(result.Stamp.SessionGeneration, published.SessionGeneration);
            Assert.Contains(published.ActivePackages, package => package.PackageId == "test.package");
            using var lease = owner.State.AcquireLease();
            Assert.Same(session, lease.Session);
            Assert.Equal(result.Stamp.SessionGeneration, lease.Generation);
            Assert.Equal(1, service.CommitCount);
        }
        finally
        {
            service.ReleaseCommit();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_BlockedCandidateKeepsPackageEventAndUiSnapshotsInternal()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-candidate-visibility-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var ui = new RuntimePackageUiService(
            owner,
            snapshots,
            new InstalledPackageStore(paths));
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var oldRoot = Path.Combine(root, "old-app");
        var candidateRoot = Path.Combine(root, "candidate-app");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            oldRoot,
            "old.app",
            "1.0.0",
            typeof(ActivePackageSessionTests).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole]);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            candidateRoot,
            "candidate.app",
            "1.0.0",
            typeof(ActivePackageSessionTests).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole]);
        var oldSource = CanonicalPackageTestBuilder.CreateTargetSource(
            oldRoot,
            PackageSourceKind.Dev).Source;
        var candidateSource = CanonicalPackageTestBuilder.CreateTargetSource(
            candidateRoot,
            PackageSourceKind.Dev).Source;
        var oldSession = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["old.app"] = CreateSessionPackage("old.app", isEnabled: true) with
                {
                    HostRoles = PackageHostRoles.App,
                },
            },
            workersStarted: false,
            readySources: [oldSource]);
        var blocker = new BlockingGenerationBackgroundService();
        var blockingPackage = CreateLoadedPackage("blocking.runtime", blocker);
        var candidateSession = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["blocking.runtime"] = blockingPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidate.app"] = CreateSessionPackage("candidate.app", isEnabled: true) with
                {
                    HostRoles = PackageHostRoles.App,
                },
                ["blocking.runtime"] = CreateSessionPackage("blocking.runtime", isEnabled: true),
            },
            workersStarted: false,
            readySources: [candidateSource]);

        try
        {
            await publisher.PublishAsync(
                oldSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            Assert.Equal("old.app", Assert.Single(ui.GetActiveSnapshots(rid)).PackageId);

            var publication = publisher.PublishAsync(
                candidateSession,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 1,
                CancellationToken.None);
            await blocker.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unpublished = owner.GetSnapshot();
            Assert.Equal(1, unpublished.SessionGeneration);
            Assert.Equal("old.app", Assert.Single(unpublished.ActivePackages).PackageId);
            Assert.Equal("old.app", Assert.Single(ui.GetActiveSnapshots(rid)).PackageId);
            Assert.Single(
                events.GetSnapshot().Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            blocker.ReleaseCommit();
            var committed = await publication.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, committed.Stamp.SessionGeneration);
            Assert.Equal("candidate.app", Assert.Single(ui.GetActiveSnapshots(rid)).PackageId);
            Assert.Equal(
                [1L, 2L],
                events.GetSnapshot().Events
                    .Where(runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged)
                    .Select(runtimeEvent => runtimeEvent.SessionGeneration));
        }
        finally
        {
            blocker.ReleaseCommit();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_RepeatedGenerationActivationFailureLeavesNoHalfStartedPackage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            new RuntimeLifecyclePolicyOptions
            {
                PackageRuntimeGenerationActivationAttempts = 2,
            });
        var service = new RepeatedlyFailingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.CommitAsync(pending));

            Assert.Contains("activation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, service.CommitCount);
            Assert.Equal(1, service.StopCount);
            Assert.Equal(0, owner.Generation);
            Assert.True(owner.State.ActiveSession.IsEmpty);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
        }
        finally
        {
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_ConcurrentGenerationFaultsKeepCatalogAtLatestGeneration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        using var rpcCatalog = new RuntimeRpcCatalog();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots,
            rpcCatalog: rpcCatalog);
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            rpcCatalog: rpcCatalog);
        var firstService = new FaultingGenerationBackgroundService();
        var secondService = new FaultingGenerationBackgroundService();
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateRpcLoadedPackage("first.package", "first.provider", firstService),
                ["second.package"] = CreateRpcLoadedPackage("second.package", "second.provider", secondService),
                ["survivor.package"] = CreateRpcLoadedPackage("survivor.package", "survivor.provider"),
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateSessionPackage("first.package", isEnabled: true),
                ["second.package"] = CreateSessionPackage("second.package", isEnabled: true),
                ["survivor.package"] = CreateSessionPackage("survivor.package", isEnabled: true),
            },
            workersStarted: false);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var publication = await publisher.CommitAsync(pending);

            firstService.Fail(new InvalidOperationException("First generation failed."));
            secondService.Fail(new InvalidOperationException("Second generation failed concurrently."));
            var firstTask = WaitForPackageAsync(
                owner,
                "first.package",
                package => package.Readiness == PackageReadinessState.Failed);
            var secondTask = WaitForPackageAsync(
                owner,
                "second.package",
                package => package.Readiness == PackageReadinessState.Failed);
            await Task.WhenAll(firstTask, secondTask);
            var first = await firstTask;
            var second = await secondTask;

            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, first.FailureOrigin);
            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, second.FailureOrigin);
            Assert.Contains("First generation failed.", first.LastError, StringComparison.Ordinal);
            Assert.Contains("Second generation failed concurrently.", second.LastError, StringComparison.Ordinal);
            Assert.Equal(publication.Stamp.SessionGeneration + 2, owner.Generation);
            Assert.Collection(
                owner.State.GetActivePackages(),
                package => Assert.Equal("survivor.package", package.PackageId));
            Assert.Equal(owner.Generation, rpcCatalog.SessionGeneration);
            var survivor = Assert.Single(rpcCatalog.GetSnapshot().Providers);
            Assert.Equal("survivor.provider", survivor.ProviderId);
            Assert.Equal(owner.Generation, survivor.SessionGeneration);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutPublishesUnavailableUntilIgnoredStartCompletes()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "candidate-session");
        Directory.CreateDirectory(sessionFolder);
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMilliseconds(40),
            PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(40),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new CancellationIgnoringStartBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var session = CreateSession(
            "test.package",
            sessionFolder,
            serviceProvider,
            false,
            backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<AggregateException>(() => publisher.CommitAsync(pending));

            Assert.Equal(1, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(sessionFolder));
            Assert.False(serviceProvider.IsDisposed);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            Assert.Contains(
                owner.GetSnapshot().Errors,
                error => error.Contains("recovery failed", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PublishSessionAsync_StopTimeoutQuarantinesRetiredSessionUntilIgnoredStopCompletes()
    {
        var state = new PackageSessionState(
            NullLogger.Instance,
            static () => { },
            static _ => { },
            TimeSpan.FromMilliseconds(50));
        var oldFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var newFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        var backgroundService = new CancellationIgnoringStopBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var oldSession = CreateSession(
            "old.package",
            oldFolder,
            serviceProvider,
            true,
            backgroundService);
        var newSession = new ActivePackageSession(
            newFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

        try
        {
            await state.PublishSessionAsync(oldSession);

            var publication = await state.PublishSessionAsync(newSession);

            Assert.Contains(publication.Warnings, warning => warning.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(oldFolder));
            Assert.False(serviceProvider.IsDisposed);

            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(oldFolder));
        }
        finally
        {
            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }
    }

    [Fact]
    public async Task DisposeAsync_InFlightGenerationCommitQuarantinesProviderUntilCallbackExits()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        var provider = new TrackingServiceProvider();
        var generationParticipant = new HangingGenerationBackgroundService();
        var session = CreateSession(
            "test.package",
            sessionFolder,
            provider,
            workersStarted: true,
            generationParticipant);

        try
        {
            var commit = session.CommitRuntimeGenerationAsync(1);
            await generationParticipant.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.DisposeAsync(TimeSpan.FromMilliseconds(40)));

            Assert.False(provider.IsDisposed);
            generationParticipant.ReleaseCommit();
            await commit.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            Assert.True(provider.IsDisposed);
        }
        finally
        {
            generationParticipant.ReleaseCommit();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_FailingServiceCleanupBlocksRollbackUntilAllServicesStop()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromSeconds(1),
            PackageWorkerCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var startedService = new TestBackgroundService();
        var failingService = new FailingStartBackgroundService();
        var session = CreateSession("test.package", startedService, failingService);
        Task<PackageSessionPublicationResult>? commit = null;

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            commit = publisher.CommitAsync(pending);
            await Task.WhenAll(
                    startedService.StopEntered.Task,
                    failingService.StopEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, startedService.StopCount);
            Assert.Equal(1, failingService.StopCount);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            Assert.False(commit.IsCompleted);

            failingService.Release();
            await Assert.ThrowsAsync<InvalidOperationException>(() => commit);

            using var lease = owner.State.AcquireLease();
            Assert.Equal(0, lease.Generation);
        }
        finally
        {
            failingService.Release();
            var pendingCommit = commit;
            if (pendingCommit is not null)
            {
                _ = await Record.ExceptionAsync(() => pendingCommit);
            }
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void CleanupStaleSessions_RemovesFoldersWithoutRunningOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var activeFolder = Path.Combine(rootPath, $"20260511190000-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var staleFolder = Path.Combine(rootPath, $"20260511190001-{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyFolder = Path.Combine(rootPath, $"20260511190002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(activeFolder);
        Directory.CreateDirectory(staleFolder);
        Directory.CreateDirectory(legacyFolder);
        try
        {
            RuntimePackageSessionDirectories.CleanupStaleSessions(rootPath);

            Assert.True(Directory.Exists(activeFolder));
            Assert.False(Directory.Exists(staleFolder));
            Assert.False(Directory.Exists(legacyFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        params TestRuntimePackageWorker[] workers)
        => CreateSession(
            packageId,
            sessionFolder: null,
            disposalWorker: null,
            workersStarted: false,
            workers: workers);

    private static async Task AssertFaultPublicationSurvivesConcurrentClearAsync(
        bool runtimeGenerationFault)
    {
        const string packageId = "faulting.package";
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events);
        var session = CreateSession(packageId, new RecordingGenerationBackgroundService());
        var publisher = new PackageSessionPublisher(
            owner,
            NullLogger<PackageSessionPublisher>.Instance);
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? fault = null;
        Task<IReadOnlyList<string>>? clear = null;
        PackageSessionState.SessionPublicationSnapshot? faultPublicationSnapshot = null;

        try
        {
            await publisher.PublishAsync(
                session,
                null,
                [],
                [],
                owner.Generation,
                CancellationToken.None);
            PackageActivationIdentity activationIdentity;
            RuntimePackageGeneration runtimeGeneration;
            using (var lease = owner.State.AcquireLease())
            {
                var package = Assert.IsType<ActiveLoadedPackage>(
                    owner.State.GetLoadedPackage(lease, packageId));
                activationIdentity = lease.GetPackageActivationIdentity(package);
                runtimeGeneration = new RuntimePackageGeneration(
                    package.RuntimeActivationId,
                    lease.Generation);
            }
            var exception = new InvalidOperationException(
                runtimeGenerationFault ? "Runtime generation failed." : "App package failed.");
            var origin = runtimeGenerationFault
                ? PackageFailureOrigin.RuntimeBackgroundService
                : PackageFailureOrigin.RuntimeRpcProvider;

            fault = Task.Run(() =>
            {
                Task Publish(long generation)
                {
                    var publicationSnapshot = owner.State.CapturePublicationSnapshot(generation);
                    faultPublicationSnapshot = publicationSnapshot;
                    publicationEntered.TrySetResult();
                    releasePublication.Task.GetAwaiter().GetResult();
                    Assert.True(owner.CommitGeneration(
                        publicationSnapshot.Generation,
                        publicationSnapshot.ActivePackages,
                        publicationSnapshot.SessionPackages,
                        owner.Sources.Snapshot(),
                        [],
                        [$"Package '{packageId}' failed: {exception.Message}"]));
                    return Task.CompletedTask;
                }

                return runtimeGenerationFault
                    ? owner.State.HandleRuntimeGenerationFault(
                        packageId,
                        runtimeGeneration,
                        expectedGeneration: 1,
                        origin,
                        exception,
                        "test Runtime generation fault publication",
                        Publish)
                    : owner.State.HandlePackageFault(
                        packageId,
                        activationIdentity,
                        expectedGeneration: 1,
                        origin,
                        exception,
                        "test app package fault publication",
                        Publish);
            });
            await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, owner.Generation);
            Assert.Equal(1, owner.GetSnapshot().SessionGeneration);
            clear = owner.State.ClearActiveSessionAsync();

            Assert.False(clear.IsCompleted);
            Assert.Same(session, owner.State.ActiveSession);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());
            releasePublication.TrySetResult();

            Assert.True(await fault.WaitAsync(TimeSpan.FromSeconds(2)));
            await clear.WaitAsync(TimeSpan.FromSeconds(2));

            var snapshot = owner.GetSnapshot();
            Assert.Equal(2, snapshot.SessionGeneration);
            Assert.Empty(snapshot.ActivePackages);
            var failedPackage = Assert.Single(snapshot.SessionPackages);
            Assert.Equal(packageId, failedPackage.PackageId);
            Assert.Equal(PackageReadinessState.Failed, failedPackage.Readiness);
            Assert.Equal(origin, failedPackage.FailureOrigin);
            Assert.Equal(exception.Message, failedPackage.LastError);
            Assert.Contains(
                $"Package '{packageId}' failed: {exception.Message}",
                snapshot.Errors);
            Assert.Equal(2, events.GetSnapshot().SessionGeneration);
            Assert.Equal(
                [1L, 2L],
                events.GetSnapshot().Events
                    .Where(runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged)
                    .Select(runtimeEvent => runtimeEvent.SessionGeneration));
            Assert.True(owner.State.ActiveSession.IsEmpty);
            var capturedFaultPublication = Assert.IsType<PackageSessionState.SessionPublicationSnapshot>(
                faultPublicationSnapshot);
            Assert.Equal(2, capturedFaultPublication.Generation);
            Assert.Empty(capturedFaultPublication.ActivePackages);
            Assert.Equal(
                PackageReadinessState.Failed,
                Assert.Single(capturedFaultPublication.SessionPackages).Readiness);
        }
        finally
        {
            releasePublication.TrySetResult();
            if (fault is not null)
            {
                await fault.WaitAsync(TimeSpan.FromSeconds(2));
            }
            if (clear is not null)
            {
                await clear.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else if (!owner.State.ActiveSession.IsEmpty)
            {
                await owner.State.ClearActiveSessionAsync();
            }
            await session.DisposeAsync();
        }
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        string? sessionFolder,
        TestRuntimePackageWorker? disposalWorker,
        bool workersStarted,
        params TestRuntimePackageWorker[] workers)
    {
        var loadedPackage = CreateLoadedPackageCore(packageId, sessionFolder, disposalWorker, workers);
        return new ActivePackageSession(
            sessionFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = CreateSessionPackage(packageId, isEnabled: true),
            },
            workersStarted: workersStarted);
    }

    private static ActivePackageSession CreateRpcSession(
        string packageId,
        params TestRuntimePackageWorker[] workers)
    {
        var loadedPackage = CreateLoadedPackage(packageId, workers) with
        {
            RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
            {
                ["example.provider"] = new RuntimeRpcProviderRegistration(
                    "example.provider",
                    "example.rpc",
                    "1.0.0",
                    new string('a', 64),
                    Contract: null!,
                    Handler: null!,
                    Manifest: new SunderPackageProviderManifest { AbiSha256 = new string('b', 64) }),
            },
        };
        return new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = CreateSessionPackage(packageId, isEnabled: true),
            },
            workersStarted: false);
    }

    private static ActiveLoadedPackage CreateRpcLoadedPackage(
        string packageId,
        string providerId,
        params TestRuntimePackageWorker[] workers)
        => CreateLoadedPackage(packageId, workers) with
        {
            RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
            {
                [providerId] = new RuntimeRpcProviderRegistration(
                    providerId,
                    "example.rpc",
                    "1.0.0",
                    new string('a', 64),
                    Contract: null!,
                    Handler: null!,
                    Manifest: new SunderPackageProviderManifest { AbiSha256 = new string('b', 64) }),
            },
        };

    private static ActiveLoadedPackage CreateLoadedPackage(
        string packageId,
        params TestRuntimePackageWorker[] workers)
        => CreateLoadedPackageCore(
            packageId,
            sessionFolder: null,
            disposalWorker: null,
            workers: workers);

    private static ActiveLoadedPackage CreateLoadedPackageCore(
        string packageId,
        string? sessionFolder,
        TestRuntimePackageWorker? disposalWorker,
        IReadOnlyList<TestRuntimePackageWorker> workers)
    {
        var tempDirectory = sessionFolder is null
            ? Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"))
            : Path.Combine(sessionFolder, "package-content");
        var storageDirectory = sessionFolder is null
            ? tempDirectory
            : TestSessionStoragePath(sessionFolder);
        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "sunder-package.json"), "{}");
        var packageWorkers = workers.Count == 0
            ? [new TestBackgroundService()]
            : workers.ToArray();
        if (disposalWorker is not null)
        {
            packageWorkers = [.. packageWorkers, disposalWorker];
        }
        TestRuntimePackageWorker worker = packageWorkers.Length == 1
            ? packageWorkers[0]
            : new CompositeTestRuntimePackageWorker(packageWorkers);
        var settingsStore = new JsonPackageKeyValueStore(Path.Combine(storageDirectory, "settings.json"));

        return new ActiveLoadedPackage(
            CreateActivePackage(packageId, isEnabled: true, PackageReadinessState.Ready),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(storageDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(storageDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            new LocalPackageFileStore(Path.Combine(storageDirectory, "files")),
            new PackageSettings(settingsStore),
            worker);
    }

    private static ActivePackageSession CreateStorageMigrationSession(
        TransactionalStorageMigrationWorker firstWorker,
        TransactionalStorageMigrationWorker secondWorker,
        RuntimePackageDataResources firstContext,
        RuntimePackageDataResources secondContext)
    {
        var packages = new[]
        {
            CreateStoragePackage("first.package", firstContext, firstWorker),
            CreateStoragePackage("second.package", secondContext, secondWorker),
        };
        return new ActivePackageSession(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => CreateSessionPackage(package.Descriptor.PackageId, isEnabled: true),
                StringComparer.OrdinalIgnoreCase),
            workersStarted: false);
    }

    private static ActiveLoadedPackage CreateStoragePackage(
        string packageId,
        RuntimePackageDataResources context,
        TransactionalStorageMigrationWorker worker)
        => new(
            CreateActivePackage(packageId, isEnabled: true, PackageReadinessState.Ready),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, context.Storage.DataRootPath),
            SettingsSchema: null,
            context.Storage.State,
            context.Secrets,
            context.Storage.Files,
            context.Settings,
            worker)
        {
            MigrationJournal = context.MigrationJournal,
        };

    private static ActiveLoadedPackage CreateWorkspacePackage(
        string root,
        string version,
        WorkspaceProbeWorker worker)
    {
        var source = Path.Combine(root, "sources", version);
        var data = Path.Combine(root, "package-data", "data");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(source, "sunder-package.json"), "{}");
        var settingsStore = new JsonPackageKeyValueStore(Path.Combine(data, "settings.json"));
        return new ActiveLoadedPackage(
            new ActivePackageDescriptor(
                "workspace.package",
                "Workspace Package",
                version,
                PackageHostRoles.Runtime,
                Icon: null,
                IsEnabled: true,
                PackageReadinessState.Ready,
                Views: []),
            new RuntimePackageSource("workspace.package", PackageSourceKind.Dev, source),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(data, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(data, "secrets.json")),
            new LocalPackageFileStore(Path.Combine(root, "package-data", "files")),
            new PackageSettings(settingsStore),
            worker);
    }

    private static ActivePackageSession CreateWorkspaceSession(ActiveLoadedPackage package)
        => new(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [package.Descriptor.PackageId] = package,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [package.Descriptor.PackageId] = CreateSessionPackage(
                    package.Descriptor.PackageId,
                    isEnabled: true) with
                {
                    Version = package.Descriptor.Version,
                },
            },
            workersStarted: false);

    private static IEnumerable<string> WorkspaceActivationBackups(string workspace)
        => Directory.EnumerateDirectories(
            Path.GetDirectoryName(workspace)!,
            $".{Path.GetFileName(workspace)}.sunder-activation-*");

    private static async Task SeedLegacyStorageAsync(RuntimePackageDataResources context, string value)
    {
        await context.Storage.State.SetValueAsync("legacy.state", value + "-state");
        await context.Storage.SettingsStore.SetValueAsync("legacy.setting", value + "-setting");
        await context.Secrets.SetSecretAsync("legacy.secret", value + "-secret");
        await context.Storage.Files.WriteAsync("legacy.file", System.Text.Encoding.UTF8.GetBytes(value + "-file"));
    }

    private static async Task AssertLegacyStorageAsync(RuntimePackageDataResources context, string value)
    {
        Assert.Equal(value + "-state", await context.Storage.State.GetValueAsync("legacy.state"));
        Assert.Null(await context.Storage.State.GetValueAsync("current.state"));
        Assert.Equal(value + "-setting", await context.Storage.SettingsStore.GetValueAsync("legacy.setting"));
        Assert.Null(await context.Storage.SettingsStore.GetValueAsync("current.setting"));
        Assert.Equal(value + "-secret", await context.Secrets.GetSecretAsync("legacy.secret"));
        Assert.Null(await context.Secrets.GetSecretAsync("current.secret"));
        Assert.Equal(
            value + "-file",
            System.Text.Encoding.UTF8.GetString((await context.Storage.Files.ReadAsync("legacy.file"))!));
        Assert.Null(await context.Storage.Files.ReadAsync("current.file"));
    }

    private static async Task AssertMigratedStorageAsync(RuntimePackageDataResources context, string value)
    {
        Assert.Null(await context.Storage.State.GetValueAsync("legacy.state"));
        Assert.Equal(value + "-state", await context.Storage.State.GetValueAsync("current.state"));
        Assert.Null(await context.Storage.SettingsStore.GetValueAsync("legacy.setting"));
        Assert.Equal(value + "-setting", await context.Storage.SettingsStore.GetValueAsync("current.setting"));
        Assert.Null(await context.Secrets.GetSecretAsync("legacy.secret"));
        Assert.Equal(value + "-secret", await context.Secrets.GetSecretAsync("current.secret"));
        Assert.Null(await context.Storage.Files.ReadAsync("legacy.file"));
        Assert.Equal(
            value + "-file",
            System.Text.Encoding.UTF8.GetString((await context.Storage.Files.ReadAsync("current.file"))!));
    }

    private static ActivePackageDescriptor CreateActivePackage(
        string packageId,
        bool isEnabled,
        PackageReadinessState readiness)
        => new(packageId, packageId, "1.0.0", PackageHostRoles.Runtime, Icon: null, isEnabled, readiness, Views: []);

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        var sessionStorage = TestSessionStoragePath(path);
        if (Directory.Exists(sessionStorage))
        {
            Directory.Delete(sessionStorage, recursive: true);
        }
    }

    private static string TestSessionStoragePath(string sessionFolder)
        => Path.Combine(
            Path.GetDirectoryName(sessionFolder)!,
            $".{Path.GetFileName(sessionFolder)}-package-data");

    private static SessionPackageDescriptor CreateSessionPackage(string packageId, bool isEnabled)
        => new(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            Icon: null,
            isEnabled,
            isEnabled ? PackageReadinessState.Ready : PackageReadinessState.Failed,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);

    private sealed class CompositeTestRuntimePackageWorker(
        IReadOnlyList<TestRuntimePackageWorker> workers) : TestRuntimePackageWorker
    {
        public override Task GenerationCompletion
            => Task.WhenAll(workers.Select(static worker => worker.GenerationCompletion));

        public override async Task StartAsync(CancellationToken cancellationToken = default)
        {
            foreach (var worker in workers)
            {
                await worker.StartAsync(cancellationToken);
            }
        }

        public override async Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            foreach (var worker in workers)
            {
                await worker.CommitGenerationAsync(generation, cancellationToken);
            }
        }

        public override async Task PrepareStorageMigrationsAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            foreach (var worker in workers)
            {
                await worker.PrepareStorageMigrationsAsync(generation, cancellationToken);
            }
        }

        public override Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
            => Task.WhenAll(workers.Select(worker => worker.ActivateGenerationAsync(cancellationToken)));

        public override Task StopAsync(CancellationToken cancellationToken = default)
            => Task.WhenAll(workers.Reverse().Select(worker => worker.StopAsync(cancellationToken)));

        public override async ValueTask DisposeAsync()
        {
            foreach (var worker in workers.Reverse())
            {
                await worker.DisposeAsync();
            }
        }
    }

    private sealed class TestBackgroundService(
        Func<long>? getGeneration = null,
        TaskCompletionSource? startEntered = null,
        Task? allowStart = null) : TestRuntimePackageWorker
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long? GenerationObservedAtStart { get; private set; }

        public override async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            GenerationObservedAtStart = getGeneration?.Invoke();
            startEntered?.TrySetResult();
            if (allowStart is not null)
            {
                await allowStart.WaitAsync(cancellationToken);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopEntered.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingAfterSignalBackgroundService(Task signal) : TestRuntimePackageWorker
    {
        public int StopCount { get; private set; }

        public override async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await signal.WaitAsync(cancellationToken);
            throw new InvalidOperationException("injected wave failure");
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class GenerationAwareBackgroundService(
        Func<long> getGeneration,
        TaskCompletionSource startEntered,
        Task allowStart) : TestRuntimePackageWorker
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }
        public long? GenerationObservedAtStart { get; private set; }
        public long? GenerationObservedAtCommit { get; private set; }
        public RuntimePackageGeneration? CommittedGeneration { get; private set; }

        public override async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            GenerationObservedAtStart = getGeneration();
            startEntered.TrySetResult();
            await allowStart.WaitAsync(cancellationToken);
        }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            GenerationObservedAtCommit = getGeneration();
            CommittedGeneration = generation;
            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PublicationObservingProcessParticipant(
        PackageSessionState sessions,
        RuntimeRpcCatalog catalog) : TestRuntimePackageWorker
    {
        private RuntimePackageGeneration? _generation;
        public bool Prepared { get; private set; }
        public bool Activated { get; private set; }
        public long? ObservedGeneration { get; private set; }
        public int ObservedProviderCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            _generation = generation;
            return Task.CompletedTask;
        }

        public override Task PrepareStorageMigrationsAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_generation, generation);
            Assert.Throws<RuntimeUnavailableException>(() => sessions.AcquireLease());
            using var activationLease = sessions.AcquirePackageActivationLease(
                "process.package",
                generation);
            Prepared = true;
            ObservedGeneration = activationLease.Generation;
            ObservedProviderCount = catalog.GetSnapshot().Providers.Count;
            return Task.CompletedTask;
        }

        public override Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Prepared);
            Assert.Empty(catalog.GetSnapshot().Providers);
            Activated = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingDuringActivationProcessParticipant : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task GenerationCompletion => _completion.Task;

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task PrepareStorageMigrationsAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completion.TrySetException(new InvalidOperationException(
                "The process faulted after activation acknowledgment."));
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessPreparationBarrier(int expectedParticipants)
    {
        private readonly TaskCompletionSource _allActivationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int PreparedCount { get; private set; }

        public int ActivationStartedCount { get; private set; }

        public void Prepared()
            => PreparedCount++;

        public void AssertComplete()
            => Assert.Equal(expectedParticipants, PreparedCount);

        public Task ActivationStarted()
        {
            ActivationStartedCount++;
            if (ActivationStartedCount == expectedParticipants)
            {
                _allActivationStarted.TrySetResult();
            }
            return _allActivationStarted.Task;
        }
    }

    private sealed class BarrierObservingProcessParticipant(ProcessPreparationBarrier barrier) : TestRuntimePackageWorker
    {
        private RuntimePackageGeneration? _generation;

        public bool Activated { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _generation = generation;
            return Task.CompletedTask;
        }

        public override Task PrepareStorageMigrationsAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_generation, generation);
            barrier.Prepared();
            return Task.CompletedTask;
        }

        public override async Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            barrier.AssertComplete();
            await barrier.ActivationStarted().WaitAsync(cancellationToken);
            Activated = true;
        }
    }

    private sealed class RecordingGenerationBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }
        public Task Stopped => _stopped.Task;

        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateFailingGenerationBackgroundService : TestRuntimePackageWorker
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            throw new InvalidOperationException("Candidate startup failed.");
        }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCommitWithBlockingStopWorker : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _releaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("Candidate generation commit failed."));

        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopEntered.TrySetResult();
            await _releaseStop.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseStop() => _releaseStop.TrySetResult();
    }

    private sealed class BlockingPolicyCleanupWorker : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _releaseStop = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopEntered.TrySetResult();
            await _releaseStop.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseStop() => _releaseStop.TrySetResult();
    }

    private sealed class HangingGenerationBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _releaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override async Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitEntered.TrySetResult();
            await _releaseCommit.Task;
        }

        public override Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReleaseCommit() => _releaseCommit.TrySetResult();
    }

    private sealed class BlockingGenerationBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _releaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CommitCount { get; private set; }
        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override async Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            using var registration = cancellationToken.Register(CancellationObserved.SetResult);
            CommitEntered.TrySetResult();
            await _releaseCommit.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopObserved.TrySetResult();
            return Task.CompletedTask;
        }

        public void ReleaseCommit() => _releaseCommit.TrySetResult();
    }

    private sealed class BlockingActivationBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _releaseActivation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ActivationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task StartAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override async Task ActivateGenerationAsync(
            CancellationToken cancellationToken = default)
        {
            ActivationEntered.TrySetResult();
            await _releaseActivation.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseActivation() => _releaseActivation.TrySetResult();
    }

    private sealed class RepeatedlyFailingGenerationBackgroundService : TestRuntimePackageWorker
    {
        public int CommitCount { get; private set; }
        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            throw new InvalidOperationException("Runtime generation activation failed.");
        }

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingGenerationBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task GenerationCompletion => _completion.Task;
        public Task Stopped => _stopped.Task;

        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _completion.TrySetResult();
            _stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class PackageDataAuthorityWorker(
        PackageSessionState sessions,
        string packageId) : TestRuntimePackageWorker
    {
        public RuntimePackageGeneration? Generation { get; private set; }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            Generation = generation;
            return Task.CompletedTask;
        }

        public async Task<(string? State, string? Secret)> RoundTripAsync()
        {
            using var lease = sessions.AcquirePackageActivationLease(
                packageId,
                Generation ?? throw new InvalidOperationException("The survivor worker was not committed."));
            var package = sessions.GetLoadedPackage(lease, packageId)
                          ?? throw new InvalidOperationException("The surviving package is unavailable.");
            await package.StateStore.SetValueAsync("survivor.state", "surviving-state");
            await package.SecretsStore.SetSecretAsync("survivor.secret", "surviving-secret");
            return (
                await package.StateStore.GetValueAsync("survivor.state"),
                await package.SecretsStore.GetSecretAsync("survivor.secret"));
        }
    }

    private sealed class TransactionalStorageMigrationWorker(
        RuntimePackageDataResources context,
        IReadOnlyList<PackageStorageMigrationPlanIdentity> plans,
        bool failActivation) : TestRuntimePackageWorker
    {
        public override IReadOnlyList<PackageStorageMigrationPlanIdentity> StorageMigrationPlans { get; } = plans;

        public override PackageStorageMigrationJournal StorageMigrationJournal => context.MigrationJournal;

        public override async Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        {
            var state = await context.Storage.State.GetValueAsync("legacy.state", cancellationToken);
            if (state is not null)
            {
                await context.Storage.State.DeleteValueAsync("legacy.state", cancellationToken);
                await context.Storage.State.SetValueAsync("current.state", state, cancellationToken);
            }
            var setting = await context.Storage.SettingsStore.GetValueAsync("legacy.setting", cancellationToken);
            if (setting is not null)
            {
                await context.Storage.SettingsStore.DeleteValueAsync("legacy.setting", cancellationToken);
                await context.Storage.SettingsStore.SetValueAsync("current.setting", setting, cancellationToken);
            }
            var secret = await context.Secrets.GetSecretAsync("legacy.secret", cancellationToken);
            if (secret is not null)
            {
                await context.Secrets.DeleteSecretAsync("legacy.secret", cancellationToken);
                await context.Secrets.SetSecretAsync("current.secret", secret, cancellationToken);
            }
            var file = await context.Storage.Files.ReadAsync("legacy.file", cancellationToken);
            if (file is not null)
            {
                await context.Storage.Files.DeleteAsync("legacy.file", cancellationToken);
                await context.Storage.Files.WriteAsync("current.file", file, cancellationToken);
            }
            if (failActivation)
            {
                throw new InvalidOperationException("Injected sibling activation failure.");
            }
        }

        public override ValueTask DisposeAsync()
        {
            context.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsupportedPackageFileStore : IPackageFileStore
    {
        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);

        public Task WriteAsync(
            string relativePath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class WorkspaceActivationProbe(string workspacePath)
    {
        private readonly object _gate = new();
        private bool _previousRunning;
        private bool _candidateRunning;

        public string WorkspacePath { get; } = workspacePath;
        public bool FailRecoveredPreviousStart { get; init; }
        public bool BlockCandidateStop { get; init; }
        public bool WorkersOverlapped { get; private set; }
        public int PreviousStartCount { get; private set; }
        public int PreviousStopCount { get; private set; }
        public int PreviousDisposeCount { get; private set; }
        public int CandidateStartCount { get; private set; }
        public int CandidateStopCount { get; private set; }
        public int CandidateDisposeCount { get; private set; }
        public long? RecoveredLeaseGeneration { get; private set; }
        public TaskCompletionSource CandidateStopEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource CandidateStopRelease { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(bool candidate)
        {
            lock (_gate)
            {
                if (candidate)
                {
                    CandidateStartCount++;
                    WorkersOverlapped |= _previousRunning;
                    _candidateRunning = true;
                    return;
                }
                PreviousStartCount++;
                WorkersOverlapped |= _candidateRunning;
                _previousRunning = true;
                if (FailRecoveredPreviousStart && PreviousStartCount > 1)
                {
                    throw new InvalidOperationException("Previous worker recovery startup failed.");
                }
            }
        }

        public async Task StopAsync(bool candidate)
        {
            lock (_gate)
            {
                if (candidate)
                {
                    CandidateStopCount++;
                    CandidateStopEntered.TrySetResult();
                }
                else
                {
                    PreviousStopCount++;
                    _previousRunning = false;
                }
            }
            if (candidate && BlockCandidateStop)
            {
                await CandidateStopRelease.Task;
            }
            if (candidate)
            {
                lock (_gate) _candidateRunning = false;
            }
        }

        public void ReleaseCandidateStop() => CandidateStopRelease.TrySetResult();

        public void Dispose(bool candidate)
        {
            lock (_gate)
            {
                if (candidate) CandidateDisposeCount++;
                else PreviousDisposeCount++;
            }
        }

        public void RecordRecoveredLease(long generation)
        {
            lock (_gate) RecoveredLeaseGeneration = generation;
        }
    }

    private sealed class WorkspaceProbeWorker(
        WorkspaceActivationProbe probe,
        bool candidate,
        PackageSessionState? sessions = null,
        bool failOnStart = true,
        bool failOnCommit = false) : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private RuntimePackageGeneration? _generation;

        public override Task GenerationCompletion => _completion.Task;

        public override string WorkspaceRootPath => probe.WorkspacePath;

        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Start(candidate);
            if (candidate)
            {
                File.WriteAllText(Path.Combine(probe.WorkspacePath, "candidate.txt"), "candidate");
                if (failOnStart)
                {
                    throw new InvalidOperationException("Candidate workspace startup failed.");
                }
            }
            return Task.CompletedTask;
        }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _generation = generation;
            if (candidate && failOnCommit)
            {
                throw new InvalidOperationException("Candidate workspace generation commit failed.");
            }
            return Task.CompletedTask;
        }

        public override Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate && probe.PreviousStartCount > 1 && sessions is not null)
            {
                using var lease = sessions.AcquirePackageActivationLease(
                    "workspace.package",
                    _generation ?? throw new InvalidOperationException("The recovered worker was not committed."));
                probe.RecordRecoveredLease(lease.Generation);
            }
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await probe.StopAsync(candidate);
            _completion.TrySetResult();
        }

        public override ValueTask DisposeAsync()
        {
            probe.Dispose(candidate);
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<SessionPackageDescriptor> WaitForPackageAsync(
        RuntimeSessionOwner owner,
        string packageId,
        Func<SessionPackageDescriptor, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            var package = owner.State.GetSessionPackage(packageId);
            if (package is not null && predicate(package))
            {
                return package;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for package '{packageId}' state.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class HangingStartBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => _release.Task;

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _release.TrySetResult();
            return Task.CompletedTask;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class CancellationIgnoringStartBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _startRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => _startRelease.Task;

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void ReleaseStart() => _startRelease.TrySetResult();
    }

    private sealed class CancellationIgnoringStopBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _stopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return _stopRelease.Task;
        }

        public void ReleaseStop() => _stopRelease.TrySetResult();
    }

    private sealed class TrackingServiceProvider(Action? onDisposing = null) : TestRuntimePackageWorker
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public override ValueTask DisposeAsync()
        {
            onDisposing?.Invoke();
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStartBackgroundService : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Background service startup failed.");

        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopEntered.TrySetResult();
            await _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

}
