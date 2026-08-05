using System.Diagnostics;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
    [Fact]
    public void LifecycleStore_PersistsStateAndFailsClosedOnCorruption()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var operation = new HostOperationDescriptor(
            "11111111111111111111111111111111",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            HostOperationKinds.RuntimeStop,
            3,
            HostOperationState.Succeeded,
            now,
            now,
            "Runtime worker is stopped.",
            null);
        var expected = new HostLifecyclePersistentState(
            HostRuntimeDesiredState.Stopped,
            3,
            null,
            [operation]);
        try
        {
            using (var store = new HostLifecycleStore(root))
            {
                Assert.Equal(HostRuntimeDesiredState.Running, store.LoadOrCreate().DesiredState);
                store.Save(expected);
            }

            using (var store = new HostLifecycleStore(root))
            {
                var actual = store.LoadOrCreate();
                Assert.Equal(expected.DesiredState, actual.DesiredState);
                Assert.Equal(expected.DeploymentGeneration, actual.DeploymentGeneration);
                Assert.Equal(expected.ActiveOperationId, actual.ActiveOperationId);
                Assert.Equal(expected.Operations, actual.Operations);
            }

            File.WriteAllText(Path.Combine(root, "runtime", "v1", "lifecycle.json"), "{\"version\":1}");
            using var corrupted = new HostLifecycleStore(root);
            Assert.Throws<InvalidDataException>(() => corrupted.LoadOrCreate());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LifecycleStore_PreventsConcurrentSupervisorOwnership()
    {
        var root = CreateTempDirectory();
        try
        {
            using var first = new HostLifecycleStore(root);

            var exception = Assert.Throws<InvalidOperationException>(() => new HostLifecycleStore(root));

            Assert.Contains("already using", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_StopMutationIsDurableAndIdempotentAcrossRestart()
    {
        var root = CreateTempDirectory();
        var connectionPath = Path.Combine(root, "connection", "worker.json");
        var request = new HostLifecycleRequest(Guid.NewGuid(), 0);
        string operationId;
        try
        {
            using (var store = new HostLifecycleStore(root))
            await using (var coordinator = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             connectionPath,
                             TimeSpan.FromSeconds(1),
                             store,
                             isRuntimeLeaseAvailable: static () => true))
            {
                using var requestLifetime = new CancellationTokenSource();
                var submission = await coordinator.SubmitStopAsync(request, requestLifetime.Token);
                requestLifetime.Cancel();
                var completed = await coordinator.WaitForOperationAsync(submission.Operation.OperationId)
                    .WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(HostOperationState.Succeeded, completed.State);
                Assert.Equal(HostRuntimeDesiredState.Stopped, coordinator.GetStatus().DesiredState);
                Assert.Equal(1, coordinator.GetStatus().DeploymentGeneration);
                Assert.Null(coordinator.GetStatus().ActiveOperationId);
                operationId = completed.OperationId;
            }

            using (var store = new HostLifecycleStore(root))
            await using (var coordinator = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             connectionPath,
                             TimeSpan.FromSeconds(1),
                             store,
                             isRuntimeLeaseAvailable: static () => true))
            {
                await coordinator.ReconcileStartupAsync(CancellationToken.None);
                var retry = await coordinator.SubmitStopAsync(request);

                Assert.Equal(operationId, retry.Operation.OperationId);
                Assert.Equal(HostOperationState.Succeeded, retry.Operation.State);
                Assert.Equal(HostRuntimeState.Stopped, coordinator.GetStatus().State);
                var conflict = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                    coordinator.SubmitStartAsync(request));
                Assert.Equal("host.mutation-reuse", conflict.Code);
                var stale = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                    coordinator.SubmitStartAsync(new HostLifecycleRequest(Guid.NewGuid(), 0)));
                Assert.Equal("host.deployment-generation-conflict", stale.Code);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_RunningOperationAllowsImmediateRetryAndConflict()
    {
        var root = CreateTempDirectory();
        var leaseAvailable = 0;
        try
        {
            using var store = new HostLifecycleStore(root);
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(5),
                store,
                isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);
            var request = new HostLifecycleRequest(Guid.NewGuid(), 0);
            var submission = await coordinator.SubmitStopAsync(request);
            await WaitForOperationStateAsync(
                coordinator,
                submission.Operation.OperationId,
                HostOperationState.Running);

            var retry = await coordinator.SubmitStopAsync(request).WaitAsync(TimeSpan.FromSeconds(1));
            var conflict = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                coordinator.SubmitStopAsync(new HostLifecycleRequest(Guid.NewGuid(), 1)));

            Assert.Equal(submission.Operation.OperationId, retry.Operation.OperationId);
            Assert.Equal("host.operation-active", conflict.Code);
            Assert.Equal(submission.Operation.OperationId, conflict.ActiveOperationId);

            Volatile.Write(ref leaseAvailable, 1);
            var completed = await coordinator.WaitForOperationAsync(submission.Operation.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HostOperationState.Succeeded, completed.State);
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_ReconcilesInterruptedOperationWithSameIdentity()
    {
        var root = CreateTempDirectory();
        var connectionPath = Path.Combine(root, "connection", "worker.json");
        var leaseAvailable = 0;
        var operation = new HostOperationDescriptor(
            "11111111111111111111111111111111",
            Guid.NewGuid(),
            HostOperationKinds.RuntimeStop,
            0,
            HostOperationState.Accepted,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);
        try
        {
            using (var store = new HostLifecycleStore(root))
            {
                store.Save(new HostLifecyclePersistentState(
                    HostRuntimeDesiredState.Stopped,
                    1,
                    operation.OperationId,
                    [operation]));
                await using var coordinator = new RuntimeWorkerCoordinator(
                    runtimeHostPath: null,
                    connectionPath,
                    TimeSpan.FromSeconds(5),
                    store,
                    isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);
                using var cancellation = new CancellationTokenSource();
                var reconciliation = coordinator.ReconcileStartupAsync(cancellation.Token);
                await WaitForOperationStateAsync(
                    coordinator,
                    operation.OperationId,
                    HostOperationState.Running);
                cancellation.Cancel();
                await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.True(coordinator.TryGetOperation(operation.OperationId, out var interrupted));
                Assert.Equal(HostOperationState.Running, interrupted!.State);
                Volatile.Write(ref leaseAvailable, 1);
            }

            Volatile.Write(ref leaseAvailable, 0);
            using (var store = new HostLifecycleStore(root))
            await using (var coordinator = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             connectionPath,
                             TimeSpan.FromSeconds(5),
                             store,
                             isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1))
            {
                var reconciliation = coordinator.ReconcileStartupAsync(CancellationToken.None);
                await WaitForOperationStateAsync(
                    coordinator,
                    operation.OperationId,
                    HostOperationState.Running);
                var exactRetry = await coordinator.SubmitStopAsync(
                        new HostLifecycleRequest(operation.MutationId, 0))
                    .WaitAsync(TimeSpan.FromSeconds(1));
                var conflict = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                    coordinator.SubmitStopAsync(new HostLifecycleRequest(Guid.NewGuid(), 1)));

                Assert.Equal(operation.OperationId, exactRetry.Operation.OperationId);
                Assert.Equal("host.operation-active", conflict.Code);

                Volatile.Write(ref leaseAvailable, 1);
                await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.True(coordinator.TryGetOperation(operation.OperationId, out var completed));
                Assert.Equal(HostOperationState.Succeeded, completed!.State);
                Assert.Equal(operation.MutationId, completed.MutationId);
                Assert.Equal(1, coordinator.GetStatus().DeploymentGeneration);
            }
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_SupervisorShutdownPreservesRunningIntent()
    {
        var root = CreateTempDirectory();
        try
        {
            using (var store = new HostLifecycleStore(root))
            await using (var coordinator = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             Path.Combine(root, "connection", "worker.json"),
                             TimeSpan.FromSeconds(1),
                             store,
                             isRuntimeLeaseAvailable: static () => true))
            {
                await coordinator.StopForSupervisorShutdownAsync();
            }

            using var reopened = new HostLifecycleStore(root);
            Assert.Equal(HostRuntimeDesiredState.Running, reopened.LoadOrCreate().DesiredState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_DurableStopPersistsIntentBeforeLeaseRelease()
    {
        var root = CreateTempDirectory();
        var leaseAvailable = 0;
        try
        {
            using var store = new HostLifecycleStore(root);
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(5),
                store,
                isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);

            var stop = coordinator.EnsureStoppedDurablyAsync();
            for (var attempt = 0; attempt < 100 && coordinator.GetStatus().ActiveOperationId is null; attempt++)
            {
                await Task.Delay(10);
            }

            var persisted = store.LoadOrCreate();
            Assert.Equal(HostRuntimeDesiredState.Stopped, persisted.DesiredState);
            Assert.Equal(1, persisted.DeploymentGeneration);
            Assert.NotNull(persisted.ActiveOperationId);
            Assert.False(stop.IsCompleted);

            Volatile.Write(ref leaseAvailable, 1);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(HostRuntimeDesiredState.Stopped, coordinator.GetStatus().DesiredState);
            Assert.Null(coordinator.GetStatus().ActiveOperationId);
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_FastShutdownBoundsRuntimeLeaseWait()
    {
        var root = CreateTempDirectory();
        var leaseAvailable = 0;
        try
        {
            using var store = new HostLifecycleStore(root);
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(30),
                store,
                isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);
            coordinator.PrepareForFastShutdown();

            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => coordinator.StopForSupervisorShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_FastShutdownCancelsRunningLifecycleWait()
    {
        var root = CreateTempDirectory();
        var leaseAvailable = 0;
        RuntimeWorkerCoordinator? coordinator = null;
        try
        {
            using var store = new HostLifecycleStore(root);
            coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(30),
                store,
                isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);
            var submission = await coordinator.SubmitStopAsync(new HostLifecycleRequest(Guid.NewGuid(), 0));
            await WaitForOperationStateAsync(
                coordinator,
                submission.Operation.OperationId,
                HostOperationState.Running);
            coordinator.PrepareForFastShutdown();

            var stopwatch = Stopwatch.StartNew();
            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            if (coordinator is not null)
            {
                await coordinator.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_RetriesPreLaunchFailuresUntilCrashLoop()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = new HostLifecycleStore(root);
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(1),
                store,
                isRuntimeLeaseAvailable: static () => true,
                getRecoveryDelay: static _ => TimeSpan.FromMilliseconds(1));

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                coordinator.ReconcileStartupAsync(CancellationToken.None));
            for (var attempt = 0; attempt < 200 && coordinator.GetStatus().State != HostRuntimeState.CrashLoop; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(HostRuntimeState.CrashLoop, coordinator.GetStatus().State);
            Assert.Equal(HostRuntimeDesiredState.Running, coordinator.GetStatus().DesiredState);
            Assert.Equal("host.worker-crash-loop", coordinator.GetStatus().FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_SerializesSubmissionBehindStartupReconciliation()
    {
        var root = CreateTempDirectory();
        var leaseAvailable = 0;
        try
        {
            using var store = new HostLifecycleStore(root);
            store.Save(HostLifecyclePersistentState.Initial with
            {
                DesiredState = HostRuntimeDesiredState.Stopped,
            });
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(5),
                store,
                isRuntimeLeaseAvailable: () => Volatile.Read(ref leaseAvailable) == 1);
            var reconciliation = coordinator.ReconcileStartupAsync(CancellationToken.None);
            await Task.Delay(50);

            var submission = coordinator.SubmitStopAsync(new HostLifecycleRequest(Guid.NewGuid(), 0));
            await Task.Delay(50);
            Assert.False(submission.IsCompleted);

            Volatile.Write(ref leaseAvailable, 1);
            await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));
            var accepted = await submission.WaitAsync(TimeSpan.FromSeconds(2));
            var completed = await coordinator.WaitForOperationAsync(accepted.Operation.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HostOperationState.Succeeded, completed.State);
            Assert.Equal(1, coordinator.GetStatus().DeploymentGeneration);
        }
        finally
        {
            Volatile.Write(ref leaseAvailable, 1);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForOperationStateAsync(
        RuntimeWorkerCoordinator coordinator,
        string operationId,
        HostOperationState expectedState)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (coordinator.TryGetOperation(operationId, out var operation)
                && operation!.State == expectedState)
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail($"Operation '{operationId}' did not reach state '{expectedState}'.");
    }
}
