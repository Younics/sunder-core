using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Settings;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageRecoveryCoordinatorTests
{
    [Fact]
    public async Task ExactRecovery_PublishesFailureFirstAndPreservesHealthyRpcActivation()
    {
        var delays = new ControlledRecoveryDelay();
        var failedWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        var healthyWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan("failed.package", "failed.provider", failedWorker, () => replacementWorker),
                new PackagePlan("healthy.package", "healthy.provider", healthyWorker),
            ],
            delays.DelayAsync);
        var failedActivationId = harness.GetLoadedPackage("failed.package").RuntimeActivationId;
        var failedEndpoint = Assert.Single(
            harness.Catalog.GetSnapshot().Providers,
            provider => provider.PackageId == "failed.package").Endpoint;
        var healthyBefore = Assert.Single(
            harness.Catalog.GetSnapshot().Providers,
            provider => provider.PackageId == "healthy.package");
        Assert.True(harness.Catalog.TryGetPackage(
            "healthy.package",
            healthyBefore.ActivationId,
            out var healthyActivation));
        Assert.True(healthyActivation!.TryAcquireCaller(4, out var healthyLease));
        using var failedPackageLease = harness.Owner.State.AcquirePackageActivationLease(
            "failed.package",
            new RuntimePackageGeneration(failedActivationId, harness.ActivationGeneration));
        replacementWorker.OnCommit = generation =>
        {
            Assert.True(harness.Catalog.TryGetPackage(
                "failed.package",
                generation.ActivationId,
                out var recoveryCandidate));
            Assert.True(recoveryCandidate!.AllowsPrePublicationLease);
            Assert.False(harness.Catalog.TryGetEndpoint(
                recoveryCandidate,
                failedEndpoint,
                out _,
                out var stale));
            Assert.True(stale);
            using var lease = harness.Owner.State.AcquirePackageActivationLease(
                "failed.package",
                generation);
            Assert.True(recoveryCandidate.IsCurrent(lease));
        };
        using (healthyLease)
        {
            failedWorker.Fail(new InvalidOperationException("worker exited"));
            var backoff = await delays.NextAsync("failed.package");
            var failed = await WaitForPackageAsync(
                harness.Owner,
                "failed.package",
                package => package.Readiness == PackageReadinessState.Failed);

            Assert.Equal(TimeSpan.FromMilliseconds(250), backoff.Delay);
            Assert.Equal(1, failed.FailureCount);
            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, failed.FailureOrigin);
            Assert.Contains("worker exited", failed.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain(
                harness.Events.GetSnapshot().Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted);
            harness.Owner.HandleRuntimeGenerationFault(
                "failed.package",
                new RuntimePackageGeneration(failedActivationId, harness.ActivationGeneration),
                new InvalidOperationException("duplicate stale callback"));

            backoff.Release();
            await Task.Delay(50);
            Assert.Equal(0, harness.GetFactoryCount("failed.package"));
            failedPackageLease.Dispose();
            await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySucceeded, "failed.package");

            var recovered = harness.GetLoadedPackage("failed.package");
            Assert.NotEqual(failedActivationId, recovered.RuntimeActivationId);
            harness.Owner.HandleRuntimeGenerationFault(
                "failed.package",
                new RuntimePackageGeneration(failedActivationId, harness.ActivationGeneration),
                new InvalidOperationException("late stale callback"));
            Assert.Equal(
                recovered.RuntimeActivationId,
                harness.GetLoadedPackage("failed.package").RuntimeActivationId);
            Assert.Equal(harness.ActivationGeneration, replacementWorker.CommittedGeneration?.SessionGeneration);
            Assert.Equal(recovered.RuntimeActivationId, replacementWorker.CommittedGeneration?.ActivationId);
            var descriptor = harness.Owner.State.GetSessionPackage("failed.package");
            Assert.NotNull(descriptor);
            Assert.True(descriptor.IsEnabled);
            Assert.Equal(PackageReadinessState.Ready, descriptor.Readiness);
            Assert.Null(descriptor.LastError);
            Assert.Null(descriptor.FailureOrigin);
            Assert.Equal(1, descriptor.FailureCount);
            Assert.NotNull(descriptor.LastFailureAtUtc);
            Assert.Equal(1, harness.GetFactoryCount("failed.package"));

            var healthyAfter = Assert.Single(
                harness.Catalog.GetSnapshot().Providers,
                provider => provider.PackageId == "healthy.package");
            Assert.Equal(healthyBefore.Endpoint, healthyAfter.Endpoint);
            Assert.Equal(healthyBefore.ActivationEpoch, healthyAfter.ActivationEpoch);
            Assert.Same(
                healthyActivation,
                Assert.IsType<RuntimeRpcPackageActivation>(GetCatalogPackage(
                    harness.Catalog,
                    "healthy.package",
                    healthyAfter.ActivationId)));
            Assert.False(healthyLease!.RetirementToken.IsCancellationRequested);
            Assert.Equal(harness.Owner.Generation, healthyAfter.SessionGeneration);

            Assert.False(harness.Catalog.TryGetActiveEndpoint(failedEndpoint, out _, out var stale));
            Assert.True(stale);
            var recoveredEndpoint = Assert.Single(
                harness.Catalog.GetSnapshot().Providers,
                provider => provider.PackageId == "failed.package").Endpoint;
            Assert.NotEqual(failedEndpoint, recoveredEndpoint);
        }
    }

    [Fact]
    public async Task RecoveryPublication_DoesNotWaitForGenerationRetirementCallback()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync);
        initialWorker.Fail(new InvalidOperationException("worker exited"));
        var backoff = await delays.NextAsync("failed.package");
        await WaitForPackageAsync(
            harness.Owner,
            "failed.package",
            package => package.Readiness == PackageReadinessState.Failed);
        CancellationToken generationRetirement;
        using (var lease = harness.Owner.State.AcquireLease())
        {
            generationRetirement = lease.GenerationRetirementToken;
        }
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = generationRetirement.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Wait();
        });

        try
        {
            backoff.Release();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySucceeded,
                "failed.package");

            Assert.Equal(1, harness.GetFactoryCount("failed.package"));
            Assert.Equal(
                replacementWorker.CommittedGeneration?.ActivationId,
                harness.GetLoadedPackage("failed.package").RuntimeActivationId);
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    [Fact]
    public async Task RecoveryFailures_UseExactBackoffAndSuspendOnSixthFailure()
    {
        var delays = new RecordingRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                static () => new RecoveryWorker(failStart: true))],
            delays.DelayAsync);

        initialWorker.Fail(new InvalidOperationException("initial failure"));
        await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySuspended, "failed.package");

        Assert.Equal(
            [250d, 500d, 1000d, 2000d, 4000d],
            delays.Delays.Select(delay => delay.TotalMilliseconds).ToArray());
        Assert.Equal(5, harness.GetFactoryCount("failed.package"));
        var failed = harness.Owner.State.GetSessionPackage("failed.package");
        Assert.NotNull(failed);
        Assert.Equal(6, failed.FailureCount);
        Assert.Equal(PackageReadinessState.Failed, failed.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeActivation, failed.FailureOrigin);
        Assert.Contains("startup failed", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(
            5,
            harness.Events.GetSnapshot().Events.Count(runtimeEvent =>
                runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
    }

    [Fact]
    public async Task FailureWindow_PrunesEarlierRecoveredActivationFailures()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        RecoveryWorker? replacementWorker = null;
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker = new RecoveryWorker())],
            delays.DelayAsync);
        initialWorker.Fail(new InvalidOperationException("first failure"));
        var firstBackoff = await delays.NextAsync("failed.package");
        Assert.Equal(TimeSpan.FromMilliseconds(250), firstBackoff.Delay);
        firstBackoff.Release();
        await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySucceeded, "failed.package");

        harness.TimeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(1));
        Assert.NotNull(replacementWorker);
        replacementWorker.Fail(new InvalidOperationException("failure outside window"));
        var secondBackoff = await delays.NextAsync("failed.package");

        Assert.Equal(TimeSpan.FromMilliseconds(250), secondBackoff.Delay);
        Assert.Equal(2, harness.Owner.State.GetSessionPackage("failed.package")!.FailureCount);
        secondBackoff.Release();
    }

    [Theory]
    [InlineData(RecoveryFenceMutation.Disable)]
    [InlineData(RecoveryFenceMutation.Update)]
    [InlineData(RecoveryFenceMutation.Uninstall)]
    public async Task DisableUpdateOrUninstallDuringBackoff_ReplacesSessionAndPreventsWorkerResurrection(
        RecoveryFenceMutation mutation)
    {
        var delays = new ControlledRecoveryDelay();
        var failedWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                failedWorker,
                static () => new RecoveryWorker())],
            delays.DelayAsync);
        failedWorker.Fail(new InvalidOperationException("worker exited"));
        var backoff = await delays.NextAsync("failed.package");
        await WaitForPackageAsync(
            harness.Owner,
            "failed.package",
            package => package.Readiness == PackageReadinessState.Failed);

        var replacement = mutation switch
        {
            RecoveryFenceMutation.Disable => harness.CreateDisabledSession("failed.package"),
            RecoveryFenceMutation.Update => harness.CreateCandidateSession(
                "failed.package",
                new RecoveryWorker(),
                version: "2.0.0"),
            RecoveryFenceMutation.Uninstall => ActivePackageSession.CreateEmpty(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await using (var operation = await harness.OperationGate.EnterAsync())
        {
            await harness.Publisher.PublishAsync(
                replacement,
                harness.Owner.Sources.Snapshot(),
                [],
                [],
                harness.Owner.Generation,
                operation.CancellationToken);
        }
        backoff.Release();
        await Task.Delay(100);

        var package = harness.Owner.State.GetSessionPackage("failed.package");
        if (mutation == RecoveryFenceMutation.Disable)
        {
            Assert.NotNull(package);
            Assert.False(package.IsEnabled);
            Assert.Equal(PackageReadinessState.Disabled, package.Readiness);
        }
        else if (mutation == RecoveryFenceMutation.Update)
        {
            Assert.NotNull(package);
            Assert.Equal("2.0.0", package.Version);
            Assert.Equal(PackageReadinessState.Ready, package.Readiness);
        }
        else
        {
            Assert.True(harness.Owner.State.ActiveSession.IsEmpty);
            Assert.Null(package);
        }
        Assert.Equal(0, harness.GetFactoryCount("failed.package"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted);
    }

    [Fact]
    public async Task DependencyUnavailable_WaitsWithoutConsumingAttemptThenRecovers()
    {
        var delays = new ControlledRecoveryDelay();
        var dependencyWorker = new RecoveryWorker();
        var dependentWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan(
                    "dependency.package",
                    "dependency.provider",
                    dependencyWorker,
                    static () => new RecoveryWorker()),
                new PackagePlan(
                    "dependent.package",
                    "dependent.provider",
                    dependentWorker,
                    static () => new RecoveryWorker(),
                    [new PackageDependencyDescriptor("dependency.package", "1.0.0")]),
            ],
            delays.DelayAsync);
        harness.FailPackage("dependency.package", new InvalidOperationException("dependency failed"));
        harness.FailPackage("dependent.package", new InvalidOperationException("dependent failed"));
        var dependencyBackoff = await delays.NextAsync("dependency.package");
        var dependentBackoff = await delays.NextAsync("dependent.package");
        await WaitForPackageAsync(
            harness.Owner,
            "dependency.package",
            package => package.Readiness == PackageReadinessState.Failed);
        await WaitForPackageAsync(
            harness.Owner,
            "dependent.package",
            package => package.Readiness == PackageReadinessState.Failed);

        dependentBackoff.Release();
        var dependencyWait = await delays.NextAsync("dependent.package");
        Assert.Equal(0, harness.GetFactoryCount("dependent.package"));

        dependencyBackoff.Release();
        await WaitForEventAsync(
            harness.Events,
            RuntimeEventKind.PackageRecoverySucceeded,
            "dependency.package");
        dependencyWait.Release();
        await WaitForEventAsync(
            harness.Events,
            RuntimeEventKind.PackageRecoverySucceeded,
            "dependent.package");

        Assert.Equal(1, harness.GetFactoryCount("dependent.package"));
        Assert.Equal(1, harness.Owner.State.GetSessionPackage("dependent.package")!.FailureCount);
    }

    [Fact]
    public async Task DependencyFailsBeforeActivationAuthority_WaitsWithoutConsumingFailure()
    {
        var delays = new ControlledRecoveryDelay();
        RecoveryHarness? harness = null;
        var failDependency = 1;
        harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan(
                    "dependency.package",
                    "dependency.provider",
                    new RecoveryWorker(),
                    static () => new RecoveryWorker()),
                new PackagePlan(
                    "dependent.package",
                    "dependent.provider",
                    new RecoveryWorker(),
                    () =>
                    {
                        if (Interlocked.Exchange(ref failDependency, 0) == 1)
                        {
                            harness!.FailPackage(
                                "dependency.package",
                                new InvalidOperationException("dependency failed during recovery preparation"));
                        }
                        return new RecoveryWorker();
                    },
                    [new PackageDependencyDescriptor("dependency.package", "1.0.0")]),
            ],
            delays.DelayAsync);
        await using (harness)
        {
            harness.FailPackage("dependent.package", new InvalidOperationException("dependent failed"));
            var dependentBackoff = await delays.NextAsync("dependent.package");
            dependentBackoff.Release();
            var dependencyBackoff = await delays.NextAsync("dependency.package");
            var dependencyWait = await delays.NextAsync("dependent.package");

            Assert.Equal(1, harness.Owner.State.GetSessionPackage("dependent.package")!.FailureCount);
            Assert.Equal(1, harness.GetFactoryCount("dependent.package"));

            dependencyBackoff.Release();
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySucceeded,
                "dependency.package");
            dependencyWait.Release();
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySucceeded,
                "dependent.package");

            Assert.Equal(1, harness.Owner.State.GetSessionPackage("dependent.package")!.FailureCount);
            Assert.Equal(2, harness.GetFactoryCount("dependent.package"));
        }
    }

    [Fact]
    public async Task ShutdownDuringBackoff_CancelsWithoutStartingReplacement()
    {
        var delays = new ControlledRecoveryDelay();
        var failedWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                failedWorker,
                static () => new RecoveryWorker())],
            delays.DelayAsync);
        failedWorker.Fail(new InvalidOperationException("worker exited"));
        _ = await delays.NextAsync("failed.package");

        await harness.Coordinator.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, harness.GetFactoryCount("failed.package"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted);
    }

    [Fact]
    public async Task DrainingSessionFault_QueuesOnlyAfterPreviousSessionIsRestored()
    {
        var delays = new ControlledRecoveryDelay();
        var previousWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "previous.package",
                "previous.provider",
                previousWorker,
                static () => new RecoveryWorker())],
            delays.DelayAsync);
        var previousSession = harness.Owner.State.ActiveSession;
        var previousPackage = harness.GetLoadedPackage("previous.package");
        var candidateWorker = new BlockingFailCommitWorker();
        var candidateSession = harness.CreateCandidateSession("candidate.package", candidateWorker);
        var rejected = harness.Publisher.PublishAsync(
            candidateSession,
            harness.Owner.Sources.Snapshot(),
            [],
            [],
            harness.Owner.Generation,
            CancellationToken.None);
        await candidateWorker.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var failedAtUtc = harness.TimeProvider.GetUtcNow();
        harness.Owner.HandleRuntimeGenerationFault(
            "previous.package",
            new RuntimePackageGeneration(
                previousPackage.RuntimeActivationId,
                harness.ActivationGeneration),
            new InvalidOperationException("faulted while draining"));
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Equal(0, harness.GetFactoryCount("previous.package"));
        Assert.False(delays.HasPending("previous.package"));

        candidateWorker.ReleaseCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);
        var backoff = await delays.NextAsync("previous.package");
        Assert.Same(previousSession, harness.Owner.State.ActiveSession);
        Assert.Equal(
            PackageReadinessState.Failed,
            harness.Owner.State.GetSessionPackage("previous.package")!.Readiness);
        Assert.Equal(
            failedAtUtc,
            harness.Owner.State.GetSessionPackage("previous.package")!.LastFailureAtUtc);

        backoff.Release();
        await WaitForEventAsync(
            harness.Events,
            RuntimeEventKind.PackageRecoverySucceeded,
            "previous.package");
        Assert.Same(previousSession, harness.Owner.State.ActiveSession);
        Assert.Equal(1, harness.GetFactoryCount("previous.package"));
    }

    [Fact]
    public async Task PublicationFailure_RevokesAuthorityCleansWorkerThenRollsBackStorage()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWorker = new RecoveryWorker();
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "mutated");
        replacementWorker.OnStopAsync = _ =>
        {
            stopEntered.TrySetResult();
            return releaseStop.Task;
        };
        var failPublication = 1;
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            packageRecoveryPublication: () =>
            {
                if (Interlocked.Exchange(ref failPublication, 0) == 1)
                {
                    throw new InvalidOperationException("publication failed");
                }
            });
        await harness.SetStateAsync("failed.package", "value", "baseline");
        initialWorker.Fail(new InvalidOperationException("initial failure"));
        var firstBackoff = await delays.NextAsync("failed.package");
        using var staleEpochLease = harness.Owner.State.AcquireLease();

        firstBackoff.Release();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("mutated", await harness.GetStateAsync("failed.package", "value"));
        releaseStop.TrySetResult();
        var secondBackoff = await delays.NextAsync("failed.package");

        Assert.Equal(TimeSpan.FromMilliseconds(500), secondBackoff.Delay);
        Assert.Equal("baseline", await harness.GetStateAsync("failed.package", "value"));
        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal(2, harness.Owner.Generation);
        Assert.True(staleEpochLease.GenerationRetirementToken.IsCancellationRequested);
        using (var freshEpochLease = harness.Owner.State.AcquireLease())
        {
            Assert.Equal(staleEpochLease.Generation, freshEpochLease.Generation);
            Assert.NotSame(
                staleEpochLease.GenerationIdentity,
                freshEpochLease.GenerationIdentity);
        }
        Assert.False(harness.Owner.State.ExecuteIfCurrent(
            staleEpochLease,
            static () => true));
        Assert.Equal(harness.Owner.Generation, harness.Catalog.SessionGeneration);
        Assert.Equal(
            2,
            harness.Owner.State.GetSessionPackage("failed.package")!.FailureCount);
        Assert.Empty(harness.Catalog.GetSnapshot().Providers);
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
    }

    [Fact]
    public async Task SynchronousRollbackFailure_FencesRecoveryRequestsHostStopAndReleasesGate()
    {
        var delays = new ControlledRecoveryDelay();
        var replacementWorker = new RecoveryWorker();
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "candidate-write");
        var initialWorker = new RecoveryWorker();
        var otherWorker = new RecoveryWorker();
        var rollbackCalls = 0;
        var hostStopCalls = 0;
        var hostStopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan(
                    "failed.package",
                    "failed.provider",
                    initialWorker,
                    () => replacementWorker),
                new PackagePlan(
                    "other.package",
                    "other.provider",
                    otherWorker,
                    static () => new RecoveryWorker()),
            ],
            delays.DelayAsync,
            packageRecoveryPublication: () => throw new InvalidOperationException("publication failed"),
            rollbackAsync: _ =>
            {
                Interlocked.Increment(ref rollbackCalls);
                throw new IOException("injected rollback failure");
            },
            requestHostStop: () =>
            {
                Interlocked.Increment(ref hostStopCalls);
                hostStopRequested.TrySetResult();
            });
        await harness.SetStateAsync("failed.package", "value", "baseline");
        initialWorker.Fail(new InvalidOperationException("initial failure"));
        var backoff = await delays.NextAsync("failed.package");

        backoff.Release();
        await hostStopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => replacementWorker.DisposeCount == 1);

        Assert.Equal(1, Volatile.Read(ref rollbackCalls));
        Assert.Equal(1, Volatile.Read(ref hostStopCalls));
        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal(0, harness.Coordinator.QuarantinedRecoveryCount);
        Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
        Assert.Equal(1, harness.GetFactoryCount("failed.package"));
        Assert.False(delays.HasPending("failed.package"));
        await Assert.ThrowsAsync<RuntimeOperationRejectedException>(async () =>
            await harness.OperationGate.EnterAsync());

        harness.FailPackage("other.package", new InvalidOperationException("failure after integrity fence"));
        await Task.Delay(100);
        Assert.Equal(0, harness.GetFactoryCount("other.package"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted
                            && runtimeEvent.PackageIds.Contains(
                                "other.package",
                                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded
                            && runtimeEvent.PackageIds.Contains(
                                "failed.package",
                                StringComparer.OrdinalIgnoreCase));

        var shutdownCleanupCalled = false;
        await harness.OperationGate.ShutdownAsync(() =>
        {
            shutdownCleanupCalled = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(shutdownCleanupCalled);
        Assert.Equal(1, Volatile.Read(ref hostStopCalls));
    }

    [Fact]
    public async Task CandidateCalls_DrainInsideOperationGateBeforeCleanupRollbackAndRetry()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        PackageSessionLease? dataLease = null;
        RuntimeRpcActivationLease? rpcLease = null;
        RuntimeRpcPackageActivation? candidate = null;
        var callsAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryHarness? harness = null;
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "worker-write");
        replacementWorker.OnCommit = generation =>
        {
            Assert.True(harness!.Catalog.TryGetPackage(
                "failed.package",
                generation.ActivationId,
                out candidate));
            Assert.True(candidate!.TryAcquireCaller(4, out rpcLease));
            dataLease = harness.Owner.State.AcquirePackageActivationLease(
                "failed.package",
                generation);
            callsAcquired.TrySetResult();
        };
        harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            packageRecoveryPublication: () => throw new InvalidOperationException("publication failed"));
        await using (harness)
        {
            await harness.SetStateAsync("failed.package", "value", "baseline");
            initialWorker.Fail(new InvalidOperationException("initial failure"));
            var firstBackoff = await delays.NextAsync("failed.package");

            firstBackoff.Release();
            await callsAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.IsType<JsonPackageKeyValueStore>(replacementWorker.StateStore)
                .SetValueAsync("value", "candidate-call-write");
            await WaitUntilAsync(() => candidate!.RetirementToken.IsCancellationRequested);
            var gateWait = harness.OperationGate.EnterAsync().AsTask();
            await Task.Delay(50);

            Assert.False(gateWait.IsCompleted);
            Assert.Equal(0, replacementWorker.StopCount);
            Assert.Equal("candidate-call-write", await harness.GetStateAsync("failed.package", "value"));
            Assert.False(candidate!.TryAcquireCaller(4, out _));
            Assert.False(delays.HasPending("failed.package"));

            dataLease!.Dispose();
            await Task.Delay(50);
            Assert.Equal(0, replacementWorker.StopCount);
            rpcLease!.Dispose();

            var secondBackoff = await delays.NextAsync("failed.package");
            await using var gateLease = await gateWait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromMilliseconds(500), secondBackoff.Delay);
            Assert.Equal(1, replacementWorker.StopCount);
            Assert.Equal(1, replacementWorker.DisposeCount);
            Assert.Equal("baseline", await harness.GetStateAsync("failed.package", "value"));
        }
    }

    [Fact]
    public async Task CandidateAuthorityTimeout_HoldsOperationGateUntilCleanupAndRollbackComplete()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageSessionLease? dataLease = null;
        RuntimeRpcActivationLease? rpcLease = null;
        RuntimeRpcPackageActivation? candidate = null;
        var callsAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryHarness? harness = null;
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "candidate-write");
        replacementWorker.OnDisposeAsync = async () =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        };
        replacementWorker.OnCommit = generation =>
        {
            Assert.True(harness!.Catalog.TryGetPackage(
                "failed.package",
                generation.ActivationId,
                out candidate));
            Assert.True(candidate!.TryAcquireCaller(4, out rpcLease));
            dataLease = harness.Owner.State.AcquirePackageActivationLease(
                "failed.package",
                generation);
            callsAcquired.TrySetResult();
        };
        harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            cleanupTimeout: TimeSpan.FromMilliseconds(40),
            packageRecoveryPublication: () => throw new InvalidOperationException("publication failed"));
        await using (harness)
        {
            await harness.SetStateAsync("failed.package", "value", "baseline");
            initialWorker.Fail(new InvalidOperationException("initial failure"));
            var backoff = await delays.NextAsync("failed.package");

            backoff.Release();
            await callsAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySuspended,
                "failed.package");
            var competingOperation = harness.OperationGate.EnterAsync().AsTask();
            await Task.Delay(50);

            Assert.Equal(1, harness.GetFactoryCount("failed.package"));
            Assert.Equal(0, replacementWorker.StopCount);
            Assert.Equal(0, replacementWorker.DisposeCount);
            Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
            Assert.False(delays.HasPending("failed.package"));
            Assert.False(competingOperation.IsCompleted);

            dataLease!.Dispose();
            await Task.Delay(50);
            Assert.Equal(0, replacementWorker.StopCount);
            Assert.False(competingOperation.IsCompleted);
            rpcLease!.Dispose();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, replacementWorker.StopCount);
            Assert.Equal(1, replacementWorker.DisposeCount);
            Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
            Assert.False(competingOperation.IsCompleted);

            releaseCleanup.TrySetResult();
            await using var operation = await competingOperation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, replacementWorker.StopCount);
            Assert.Equal(1, replacementWorker.DisposeCount);
            Assert.Equal("baseline", await harness.GetStateAsync("failed.package", "value"));
            Assert.Equal(1, harness.GetFactoryCount("failed.package"));
            Assert.False(delays.HasPending("failed.package"));
        }
    }

    [Fact]
    public async Task DeferredRollbackFailure_RemovesQuarantineRequestsHostStopAndReleasesGate()
    {
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        PackageSessionLease? dataLease = null;
        RuntimeRpcActivationLease? rpcLease = null;
        RuntimeRpcPackageActivation? candidate = null;
        var callsAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollbackCalls = 0;
        var hostStopCalls = 0;
        var hostStopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryHarness? harness = null;
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "candidate-write");
        replacementWorker.OnCommit = generation =>
        {
            Assert.True(harness!.Catalog.TryGetPackage(
                "failed.package",
                generation.ActivationId,
                out candidate));
            Assert.True(candidate!.TryAcquireCaller(4, out rpcLease));
            dataLease = harness.Owner.State.AcquirePackageActivationLease(
                "failed.package",
                generation);
            callsAcquired.TrySetResult();
        };
        harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            cleanupTimeout: TimeSpan.FromMilliseconds(40),
            packageRecoveryPublication: () => throw new InvalidOperationException("publication failed"),
            rollbackAsync: _ =>
            {
                Interlocked.Increment(ref rollbackCalls);
                return Task.FromException(new IOException("injected deferred rollback failure"));
            },
            requestHostStop: () =>
            {
                Interlocked.Increment(ref hostStopCalls);
                hostStopRequested.TrySetResult();
            });
        await using (harness)
        {
            await harness.SetStateAsync("failed.package", "value", "baseline");
            initialWorker.Fail(new InvalidOperationException("initial failure"));
            var backoff = await delays.NextAsync("failed.package");

            backoff.Release();
            await callsAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySuspended,
                "failed.package");
            Assert.Equal(1, harness.Coordinator.QuarantinedRecoveryCount);
            var competingOperation = harness.OperationGate.EnterAsync().AsTask();
            await Task.Delay(50);
            Assert.False(competingOperation.IsCompleted);

            dataLease!.Dispose();
            rpcLease!.Dispose();
            await hostStopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => harness.Coordinator.QuarantinedRecoveryCount == 0);

            var competingFailure = await Record.ExceptionAsync(async () =>
                await competingOperation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(
                competingFailure is OperationCanceledException or InvalidOperationException,
                $"Unexpected competing operation result: {competingFailure}");
            Assert.Equal(1, Volatile.Read(ref rollbackCalls));
            Assert.Equal(1, Volatile.Read(ref hostStopCalls));
            Assert.Equal(1, replacementWorker.StopCount);
            Assert.Equal(1, replacementWorker.DisposeCount);
            Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
            Assert.Equal(1, harness.GetFactoryCount("failed.package"));
            Assert.False(delays.HasPending("failed.package"));
            Assert.DoesNotContain(
                harness.Events.GetSnapshot().Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
            await Assert.ThrowsAsync<RuntimeOperationRejectedException>(async () =>
                await harness.OperationGate.EnterAsync());

            var shutdownCleanupCalled = false;
            await harness.OperationGate.ShutdownAsync(() =>
            {
                shutdownCleanupCalled = true;
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(shutdownCleanupCalled);
            Assert.Equal(1, Volatile.Read(ref hostStopCalls));
        }
    }

    [Fact]
    public async Task SessionRetirement_FaultedPackageCleanupDoesNotSkipHealthySiblingCleanup()
    {
        var failedWorker = new RecoveryWorker
        {
            OnDisposeAsync = () => ValueTask.FromException(
                new InvalidOperationException("failed worker disposal")),
        };
        var healthyWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan("failed.package", "failed.provider", failedWorker),
                new PackagePlan("healthy.package", "healthy.provider", healthyWorker),
            ],
            static (_, _, _) => Task.CompletedTask,
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        harness.FailPackage("failed.package", new InvalidOperationException("worker failed"));
        await WaitUntilAsync(() => failedWorker.DisposeCount == 1);

        var warnings = await harness.Owner.State.ClearActiveSessionAsync();

        Assert.Contains(
            warnings,
            warning => warning.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, healthyWorker.StopCount);
        Assert.Equal(1, healthyWorker.DisposeCount);
        Assert.True(harness.Owner.State.ActiveSession.IsEmpty);
    }

    [Fact]
    public async Task DependencyFailureAtPublication_CleansReplacementBeforeStorageRollbackAndWait()
    {
        var delays = new ControlledRecoveryDelay();
        RecoveryHarness? harness = null;
        var dependencyWorker = new RecoveryWorker();
        var replacementWorker = new RecoveryWorker();
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "mutated");
        replacementWorker.OnActivateAsync = _ =>
        {
            harness!.FailPackage(
                "dependency.package",
                new InvalidOperationException("dependency failed before publication"));
            return Task.CompletedTask;
        };
        harness = await RecoveryHarness.CreateAsync(
            [
                new PackagePlan(
                    "dependency.package",
                    "dependency.provider",
                    dependencyWorker,
                    static () => new RecoveryWorker()),
                new PackagePlan(
                    "dependent.package",
                    "dependent.provider",
                    new RecoveryWorker(),
                    () => replacementWorker,
                    [new PackageDependencyDescriptor("dependency.package", "1.0.0")]),
            ],
            delays.DelayAsync);
        await using (harness)
        {
            await harness.SetStateAsync("dependent.package", "value", "baseline");
            harness.FailPackage("dependent.package", new InvalidOperationException("dependent failed"));
            var dependentBackoff = await delays.NextAsync("dependent.package");

            dependentBackoff.Release();
            _ = await delays.NextAsync("dependency.package");
            _ = await delays.NextAsync("dependent.package");

            Assert.Equal("baseline", await harness.GetStateAsync("dependent.package", "value"));
            Assert.Equal(1, replacementWorker.StopCount);
            Assert.Equal(1, replacementWorker.DisposeCount);
            Assert.Equal(1, harness.Owner.State.GetSessionPackage("dependent.package")!.FailureCount);
        }
    }

    [Fact]
    public async Task FailedWorkerCleanupCannotBeProven_SuspendsWithoutCreatingReplacement()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialWorker = new RecoveryWorker
        {
            OnStopAsync = _ => never.Task,
            OnDisposeAsync = () => new ValueTask(never.Task),
        };
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                static () => new RecoveryWorker())],
            static (_, _, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            cleanupTimeout: TimeSpan.FromMilliseconds(40));

        initialWorker.Fail(new InvalidOperationException("worker exited"));
        await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySuspended, "failed.package");

        Assert.Equal(0, harness.GetFactoryCount("failed.package"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted);
        never.TrySetResult();
    }

    [Fact]
    public async Task GenerationRetirementCallbackCannotBeProven_SuspendsRecoveryAndDoesNotStallShutdown()
    {
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                static () => new RecoveryWorker())],
            static (_, _, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            cleanupTimeout: TimeSpan.FromMilliseconds(40));
        var failedPackage = harness.GetLoadedPackage("failed.package");
        CancellationToken generationRetirement;
        using (var lease = harness.Owner.State.AcquireLease())
        {
            generationRetirement = lease.GenerationRetirementToken;
        }
        using var callback = generationRetirement.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Wait();
        });

        try
        {
            var fault = Task.Run(() => harness.Owner.HandleRuntimeGenerationFault(
                "failed.package",
                new RuntimePackageGeneration(
                    failedPackage.RuntimeActivationId,
                    harness.ActivationGeneration),
                new InvalidOperationException("worker exited")));

            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await fault.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForEventAsync(
                harness.Events,
                RuntimeEventKind.PackageRecoverySuspended,
                "failed.package");

            Assert.Equal(0, harness.GetFactoryCount("failed.package"));
            Assert.Equal(0, initialWorker.StopCount);
            Assert.DoesNotContain(
                harness.Events.GetSnapshot().Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoveryStarted);

            var warnings = await harness.Owner.State.ClearActiveSessionAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(
                warnings,
                warning => warning.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
            Assert.True(harness.Owner.State.ActiveSession.IsEmpty);
            Assert.Equal(0, initialWorker.StopCount);
        }
        finally
        {
            releaseCallback.Set();
        }

        await WaitUntilAsync(() => initialWorker.DisposeCount == 1);
        Assert.Equal(1, initialWorker.StopCount);
        Assert.Equal(1, initialWorker.DisposeCount);
        Assert.Equal(0, harness.GetFactoryCount("failed.package"));
    }

    [Fact]
    public async Task RejectedReplacementCleanupTimeout_HoldsGateUntilExistingCleanupCompletes()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWorker = new RecoveryWorker
        {
            OnStopAsync = _ => never.Task,
            OnDisposeAsync = () => new ValueTask(never.Task),
        };
        replacementWorker.OnStartAsync = async _ =>
        {
            await Assert.IsType<JsonPackageKeyValueStore>(replacementWorker.StateStore)
                .SetValueAsync("value", "candidate-write");
            throw new InvalidOperationException("replacement startup failed");
        };
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
             delays.DelayAsync,
             cleanupTimeout: TimeSpan.FromMilliseconds(40));
        await harness.SetStateAsync("failed.package", "value", "baseline");
        initialWorker.Fail(new InvalidOperationException("worker exited"));
        var backoff = await delays.NextAsync("failed.package");

        backoff.Release();
        await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySuspended, "failed.package");
        var competingOperation = harness.OperationGate.EnterAsync().AsTask();
        await Task.Delay(50);

        Assert.Equal(1, harness.GetFactoryCount("failed.package"));
        Assert.False(delays.HasPending("failed.package"));
        Assert.False(competingOperation.IsCompleted);
        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);

        never.TrySetResult();
        await using var operation = await competingOperation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal("baseline", await harness.GetStateAsync("failed.package", "value"));
    }

    [Fact]
    public async Task ShutdownAndCoordinatorDispose_AllowTrackedQuarantineToFinishWithoutResurrection()
    {
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWorker = new RecoveryWorker
        {
            OnStopAsync = _ => releaseCleanup.Task,
            OnDisposeAsync = () => new ValueTask(releaseCleanup.Task),
        };
        replacementWorker.OnStartAsync = async _ =>
        {
            await Assert.IsType<JsonPackageKeyValueStore>(replacementWorker.StateStore)
                .SetValueAsync("value", "candidate-write");
            throw new InvalidOperationException("replacement startup failed");
        };
        var delays = new ControlledRecoveryDelay();
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            cleanupTimeout: TimeSpan.FromMilliseconds(40),
            shutdownTimeout: TimeSpan.FromMilliseconds(40));
        await harness.SetStateAsync("failed.package", "value", "baseline");
        initialWorker.Fail(new InvalidOperationException("worker exited"));
        var backoff = await delays.NextAsync("failed.package");

        backoff.Release();
        await WaitForEventAsync(harness.Events, RuntimeEventKind.PackageRecoverySuspended, "failed.package");
        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.OperationGate.ShutdownAsync(static () => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        await harness.Coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("candidate-write", await harness.GetStateAsync("failed.package", "value"));
        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal(1, harness.GetFactoryCount("failed.package"));

        releaseCleanup.TrySetResult();
        await WaitUntilAsync(async () =>
            string.Equals(
                await harness.GetStateAsync("failed.package", "value"),
                "baseline",
                StringComparison.Ordinal));

        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Equal(1, harness.GetFactoryCount("failed.package"));
        Assert.False(delays.HasPending("failed.package"));
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
    }

    [Fact]
    public async Task AttemptFailureTimestamp_IsCapturedBeforeSlowCleanup()
    {
        var delays = new ControlledRecoveryDelay();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWorker = new RecoveryWorker(failStart: true)
        {
            OnStopAsync = _ =>
            {
                stopEntered.TrySetResult();
                return releaseStop.Task;
            },
        };
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync);
        initialWorker.Fail(new InvalidOperationException("worker exited"));
        var firstBackoff = await delays.NextAsync("failed.package");
        var attemptFailedAtUtc = harness.TimeProvider.GetUtcNow();

        firstBackoff.Release();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        releaseStop.TrySetResult();
        var secondBackoff = await delays.NextAsync("failed.package");

        Assert.Equal(TimeSpan.FromMilliseconds(500), secondBackoff.Delay);
        Assert.Equal(
            attemptFailedAtUtc,
            harness.Owner.State.GetSessionPackage("failed.package")!.LastFailureAtUtc);
    }

    [Fact]
    public async Task AsynchronousFaultAfterMonitorAttachment_WinsBeforeSuccessPublication()
    {
        var delays = new ControlledRecoveryDelay();
        var successGateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccessGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWorker = new RecoveryWorker();
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            beforeRecoverySuccess: async cancellationToken =>
            {
                successGateEntered.TrySetResult();
                await releaseSuccessGate.Task.WaitAsync(cancellationToken);
            });
        initialWorker.Fail(new InvalidOperationException("initial failure"));
        var backoff = await delays.NextAsync("failed.package");

        backoff.Release();
        await successGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        replacementWorker.Fail(new InvalidOperationException("asynchronous replacement fault"));
        await WaitForPackageAsync(
            harness.Owner,
            "failed.package",
            package => package.FailureCount == 2);
        releaseSuccessGate.TrySetResult();
        _ = await delays.NextAsync("failed.package");

        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
        Assert.Empty(harness.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task ShutdownAfterActivationBeforeCommit_DiscardsCandidateAndRollsBackStorage()
    {
        using var hostStopping = new CancellationTokenSource();
        var delays = new ControlledRecoveryDelay();
        var replacementWorker = new RecoveryWorker();
        var cancellationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replacementWorker.OnStartAsync = async _ => await Assert.IsType<JsonPackageKeyValueStore>(
            replacementWorker.StateStore).SetValueAsync("value", "mutated");
        replacementWorker.OnGenerationCompletionRead = () =>
        {
            hostStopping.Cancel();
            cancellationReached.TrySetResult();
        };
        var initialWorker = new RecoveryWorker();
        await using var harness = await RecoveryHarness.CreateAsync(
            [new PackagePlan(
                "failed.package",
                "failed.provider",
                initialWorker,
                () => replacementWorker)],
            delays.DelayAsync,
            hostStopping: hostStopping.Token);
        await harness.SetStateAsync("failed.package", "value", "baseline");
        initialWorker.Fail(new InvalidOperationException("initial failure"));
        var backoff = await delays.NextAsync("failed.package");

        backoff.Release();
        await cancellationReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("baseline", await harness.GetStateAsync("failed.package", "value"));
        Assert.Equal(1, replacementWorker.StopCount);
        Assert.Equal(1, replacementWorker.DisposeCount);
        Assert.Empty(harness.Catalog.GetSnapshot().Providers);
        Assert.DoesNotContain(
            harness.Events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
    }

    [Fact]
    public void RecoveryEvent_DoesNotPublishRegressingGeneration()
    {
        var events = new RuntimeEventStreamService();
        events.PublishSessionGeneration(5, []);

        var published = events.PublishPackageRecovery(
            RuntimeEventKind.PackageRecoverySucceeded,
            generation: 4,
            packageId: "failed.package",
            success: true,
            message: "stale success");

        Assert.Null(published);
        Assert.DoesNotContain(
            events.GetSnapshot().Events,
            runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.PackageRecoverySucceeded);
    }

    private static RuntimeRpcPackageActivation GetCatalogPackage(
        RuntimeRpcCatalog catalog,
        string packageId,
        Guid activationId)
    {
        Assert.True(catalog.TryGetPackage(packageId, activationId, out var package));
        return Assert.IsType<RuntimeRpcPackageActivation>(package);
    }

    private static async Task<SessionPackageDescriptor> WaitForPackageAsync(
        RuntimeSessionOwner owner,
        string packageId,
        Func<SessionPackageDescriptor, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var package = owner.State.GetSessionPackage(packageId);
            if (package is not null && predicate(package))
            {
                return package;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Package '{packageId}' did not reach the expected state.");
    }

    private static async Task<RuntimeEventDescriptor> WaitForEventAsync(
        RuntimeEventStreamService events,
        RuntimeEventKind kind,
        string packageId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runtimeEvent = events.GetSnapshot().Events.LastOrDefault(item =>
                item.Kind == kind
                && item.PackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase));
            if (runtimeEvent is not null)
            {
                return runtimeEvent;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Runtime event '{kind}' was not published for '{packageId}'.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected recovery condition was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected recovery condition was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private sealed record PackagePlan(
        string PackageId,
        string ProviderId,
        RecoveryWorker InitialWorker,
        Func<RecoveryWorker>? CreateReplacement = null,
        IReadOnlyList<PackageDependencyDescriptor>? Dependencies = null,
        string Version = "1.0.0");

    public enum RecoveryFenceMutation
    {
        Disable,
        Update,
        Uninstall,
    }

    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ConcurrentDictionary<string, int> _factoryCounts =
            new(StringComparer.OrdinalIgnoreCase);

        private RecoveryHarness(
            string root,
            RuntimeSessionOwner owner,
            RuntimeOperationGate operationGate,
            RuntimeEventStreamService events,
            RuntimeRpcCatalog catalog,
            PackageSessionPublisher publisher,
            RuntimePackageRecoveryCoordinator coordinator,
            MutableTimeProvider timeProvider,
            long activationGeneration)
        {
            _root = root;
            Owner = owner;
            OperationGate = operationGate;
            Events = events;
            Catalog = catalog;
            Publisher = publisher;
            Coordinator = coordinator;
            TimeProvider = timeProvider;
            ActivationGeneration = activationGeneration;
        }

        public RuntimeSessionOwner Owner { get; }
        public RuntimeOperationGate OperationGate { get; }
        public RuntimeEventStreamService Events { get; }
        public RuntimeRpcCatalog Catalog { get; }
        public PackageSessionPublisher Publisher { get; }
        public RuntimePackageRecoveryCoordinator Coordinator { get; }
        public MutableTimeProvider TimeProvider { get; }
        public long ActivationGeneration { get; }

        public static async Task<RecoveryHarness> CreateAsync(
            IReadOnlyList<PackagePlan> plans,
            Func<string, TimeSpan, CancellationToken, Task> delayAsync,
            TimeSpan? cleanupTimeout = null,
            TimeSpan? shutdownTimeout = null,
            CancellationToken hostStopping = default,
            Action? packageRecoveryPublication = null,
            Func<CancellationToken, Task>? beforeRecoverySuccess = null,
            Func<PackageStorageActivationTransaction, Task>? rollbackAsync = null,
            Action? requestHostStop = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "sunder-runtime-recovery-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var timeProvider = new MutableTimeProvider(
                DateTimeOffset.Parse("2026-08-15T12:00:00Z"));
            var lifecyclePolicy = new RuntimeLifecyclePolicyOptions
            {
                SessionDrainTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(1),
                PackageWorkerStartupTimeout = TimeSpan.FromSeconds(2),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromSeconds(2),
                PackageWorkerCleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(1),
                ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(1),
            };
            var events = new RuntimeEventStreamService(timeProvider: timeProvider);
            var queue = new RuntimePackageRecoveryQueue();
            var catalog = new RuntimeRpcCatalog();
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                events,
                timeProvider: timeProvider,
                lifecyclePolicy: lifecyclePolicy,
                rpcCatalog: catalog,
                recoveryQueue: queue,
                packageRecoveryPublication: packageRecoveryPublication);
            var gate = new RuntimeOperationGate(events, lifecyclePolicy);
            var publisher = new PackageSessionPublisher(
                owner,
                NullLogger<PackageSessionPublisher>.Instance,
                lifecyclePolicy,
                catalog,
                timeProvider: timeProvider);
            var coordinator = new RuntimePackageRecoveryCoordinator(
                queue,
                owner,
                gate,
                catalog,
                events,
                new RuntimePackageRecoveryPolicyOptions(),
                lifecyclePolicy,
                timeProvider,
                NullLogger<RuntimePackageRecoveryCoordinator>.Instance,
                delayAsync,
                hostStopping,
                beforeRecoverySuccess,
                rollbackAsync,
                requestHostStop);
            var harness = new RecoveryHarness(
                root,
                owner,
                gate,
                events,
                catalog,
                publisher,
                coordinator,
                timeProvider,
                activationGeneration: 1);

            var sources = plans.ToDictionary(
                plan => plan.PackageId,
                plan => harness.CreateSource(plan),
                StringComparer.OrdinalIgnoreCase);
            var loaded = new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                Func<ActiveLoadedPackage>? recoveryFactory = null;
                if (plan.CreateReplacement is not null)
                {
                    recoveryFactory = () =>
                    {
                        harness._factoryCounts.AddOrUpdate(plan.PackageId, 1, static (_, count) => count + 1);
                        return harness.CreateLoadedPackage(
                            plan,
                            sources[plan.PackageId],
                            plan.CreateReplacement(),
                            recoveryFactory);
                    };
                }
                loaded.Add(
                    plan.PackageId,
                    harness.CreateLoadedPackage(
                        plan,
                        sources[plan.PackageId],
                        plan.InitialWorker,
                        recoveryFactory));
            }
            var session = new ActivePackageSession(
                sessionFolder: null,
                loaded,
                plans.ToDictionary(
                    plan => plan.PackageId,
                    plan => CreateSessionPackage(plan.PackageId, plan.Version),
                    StringComparer.OrdinalIgnoreCase),
                workersStarted: false,
                readySources: sources.Values);
            await coordinator.StartAsync(CancellationToken.None);
            await publisher.PublishAsync(
                session,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0,
                CancellationToken.None);
            return harness;
        }

        public ActiveLoadedPackage GetLoadedPackage(string packageId)
        {
            using var lease = Owner.State.AcquireLease();
            return Assert.IsType<ActiveLoadedPackage>(
                Owner.State.GetLoadedPackage(lease, packageId));
        }

        public int GetFactoryCount(string packageId)
            => _factoryCounts.GetValueOrDefault(packageId);

        public Task SetStateAsync(string packageId, string key, string value)
            => CreateStateStore(packageId).SetValueAsync(key, value);

        public Task<string?> GetStateAsync(string packageId, string key)
            => CreateStateStore(packageId).GetValueAsync(key);

        public void FailPackage(string packageId, Exception exception)
        {
            var package = GetLoadedPackage(packageId);
            Owner.HandleRuntimeGenerationFault(
                packageId,
                new RuntimePackageGeneration(package.RuntimeActivationId, ActivationGeneration),
                exception);
        }

        public ActivePackageSession CreateCandidateSession(
            string packageId,
            RecoveryWorker worker,
            string version = "1.0.0")
        {
            var plan = new PackagePlan(
                packageId,
                packageId + ".provider",
                worker,
                Version: version);
            var source = CreateSource(plan);
            var loaded = CreateLoadedPackage(plan, source, worker, recoveryFactory: null);
            return new ActivePackageSession(
                sessionFolder: null,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = loaded,
                },
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = CreateSessionPackage(packageId, version),
                },
                workersStarted: false,
                readySources: [source]);
        }

        public ActivePackageSession CreateDisabledSession(string packageId)
            => new(
                sessionFolder: null,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [packageId] = CreateSessionPackage(packageId, enabled: false),
                },
                workersStarted: false,
                readySources: []);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            if (!Owner.State.ActiveSession.IsEmpty)
            {
                await Owner.State.ClearActiveSessionAsync();
            }
            Catalog.Dispose();
            await OperationGate.DisposeAsync();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        private RuntimePackageSource CreateSource(PackagePlan plan)
        {
            var sourcePath = Path.Combine(_root, "sources", plan.PackageId);
            Directory.CreateDirectory(sourcePath);
            File.WriteAllText(Path.Combine(sourcePath, "sunder-package.json"), "{}");
            return new RuntimePackageSource(
                plan.PackageId,
                PackageSourceKind.Dev,
                sourcePath,
                Dependencies: plan.Dependencies);
        }

        private ActiveLoadedPackage CreateLoadedPackage(
            PackagePlan plan,
            RuntimePackageSource source,
            RecoveryWorker worker,
            Func<ActiveLoadedPackage>? recoveryFactory)
        {
            var dataPath = Path.Combine(_root, "data", plan.PackageId);
            Directory.CreateDirectory(dataPath);
            var settingsStore = new JsonPackageKeyValueStore(Path.Combine(dataPath, "settings.json"));
            var stateStore = CreateStateStore(plan.PackageId);
            worker.StateStore = stateStore;
            return new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    plan.PackageId,
                    plan.PackageId,
                    plan.Version,
                    PackageHostRoles.Runtime,
                    Icon: null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    Views: []),
                source,
                SettingsSchema: null,
                stateStore,
                new JsonPackageSecretsStore(Path.Combine(dataPath, "secrets.json")),
                new LocalPackageFileStore(Path.Combine(dataPath, "files")),
                new PackageSettings(settingsStore),
                worker)
            {
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
                {
                    [plan.ProviderId] = new RuntimeRpcProviderRegistration(
                        plan.ProviderId,
                        "example.rpc",
                        "1.0.0",
                        new string('a', 64),
                        Contract: null!,
                        Handler: null!,
                        Manifest: new SunderPackageProviderManifest
                        {
                            AbiSha256 = new string('b', 64),
                        }),
                },
                RecoveryFactory = recoveryFactory,
            };
        }

        private JsonPackageKeyValueStore CreateStateStore(string packageId)
            => new(Path.Combine(_root, "data", packageId, "state.json"));

        private static SessionPackageDescriptor CreateSessionPackage(
            string packageId,
            string version = "1.0.0",
            bool enabled = true)
            => new(
                packageId,
                packageId,
                version,
                PackageHostRoles.Runtime,
                Icon: null,
                IsEnabled: enabled,
                enabled ? PackageReadinessState.Ready : PackageReadinessState.Disabled,
                Views: [],
                FailureOrigin: null,
                LastError: null,
                LastFailureAtUtc: null,
                FailureCount: 0);
    }

    private class RecoveryWorker(bool failStart = false) : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task GenerationCompletion
        {
            get
            {
                OnGenerationCompletionRead?.Invoke();
                return _completion.Task;
            }
        }

        public JsonPackageKeyValueStore? StateStore { get; set; }

        public RuntimePackageGeneration? CommittedGeneration { get; private set; }

        public Action<RuntimePackageGeneration>? OnCommit { get; set; }

        public Func<CancellationToken, Task>? OnStartAsync { get; set; }

        public Func<CancellationToken, Task>? OnActivateAsync { get; set; }

        public Func<CancellationToken, Task>? OnStopAsync { get; set; }

        public Func<ValueTask>? OnDisposeAsync { get; set; }

        public Action? OnGenerationCompletionRead { get; set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failStart)
            {
                return Task.FromException(new InvalidOperationException("replacement startup failed"));
            }
            return OnStartAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public override Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommittedGeneration = generation;
            OnCommit?.Invoke(generation);
            return Task.CompletedTask;
        }

        public override Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
            => OnActivateAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;

        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (OnStopAsync is not null)
            {
                await OnStopAsync(cancellationToken);
            }
            _completion.TrySetResult();
        }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (OnDisposeAsync is not null)
            {
                await OnDisposeAsync();
            }
            _completion.TrySetResult();
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class BlockingFailCommitWorker : RecoveryWorker
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task CommitGenerationAsync(
            RuntimePackageGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("candidate commit failed");
        }

        public void ReleaseCommit() => _release.TrySetResult();
    }

    private sealed class ControlledRecoveryDelay
    {
        private readonly ConcurrentDictionary<string, Channel<DelayCall>> _calls =
            new(StringComparer.OrdinalIgnoreCase);

        public Task DelayAsync(string packageId, TimeSpan delay, CancellationToken cancellationToken)
        {
            var call = new DelayCall(delay, cancellationToken);
            _calls.GetOrAdd(packageId, static _ => Channel.CreateUnbounded<DelayCall>())
                .Writer.TryWrite(call);
            return call.Completion;
        }

        public async Task<DelayCall> NextAsync(string packageId)
            => await _calls.GetOrAdd(packageId, static _ => Channel.CreateUnbounded<DelayCall>())
                .Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public bool HasPending(string packageId)
            => _calls.TryGetValue(packageId, out var calls) && calls.Reader.TryPeek(out _);
    }

    private sealed class DelayCall
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellation;

        public DelayCall(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay = delay;
            _cancellation = cancellationToken.Register(static state =>
                ((TaskCompletionSource)state!).TrySetCanceled(), _completion);
            _ = _completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                _cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public TimeSpan Delay { get; }
        public Task Completion => _completion.Task;
        public void Release() => _completion.TrySetResult();
    }

    private sealed class RecordingRecoveryDelay
    {
        private readonly object _gate = new();
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_gate) return _delays.ToArray();
            }
        }

        public Task DelayAsync(string packageId, TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
