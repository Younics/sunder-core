using System.Diagnostics;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Host;
using Sunder.Runtime.LocalState;
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
            [operation],
            ResetRequired: true);
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
                Assert.True(actual.ResetRequired);
            }

            var path = Path.Combine(root, "runtime", "v1", "lifecycle.json");
            var previousFormat = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            previousFormat["version"] = 1;
            previousFormat.Remove("resetRequired");
            var unsupported = previousFormat.ToJsonString();
            File.WriteAllText(path, unsupported);
            using (var store = new HostLifecycleStore(root))
            {
                Assert.Throws<InvalidDataException>(() => store.LoadOrCreate());
                Assert.Equal(unsupported, File.ReadAllText(path));
            }

            File.WriteAllText(path, "{broken");
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
            await Eventually.ObserveAsync(
                _ => ValueTask.FromResult(coordinator.GetStatus()),
                static status => status.ActiveOperationId is not null,
                TimeSpan.FromSeconds(2),
                describe: (status, error) => $"status={status}; error={error?.Message}");

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
    public async Task RuntimeWorkerCoordinator_ResetBlocksRestartUntilTerminalReceipt()
    {
        var root = CreateTempDirectory();
        var hostRoot = Path.Combine(root, "host");
        var runtimeRoot = Path.Combine(root, "runtime");
        using var resetStarted = new ManualResetEventSlim();
        using var releaseReset = new ManualResetEventSlim();
        try
        {
            RuntimeLocalState.EnsureInitialized(runtimeRoot);
            using var store = new HostLifecycleStore(hostRoot);
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath: null,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(5),
                store,
                isRuntimeLeaseAvailable: static () => true,
                runtimeStateRoot: runtimeRoot);

            var reset = Task.Run(() => coordinator.ResetRuntimeStateAsync(
                startupCacheOnly: false,
                afterStarted: () =>
                {
                    resetStarted.Set();
                    if (!releaseReset.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Timed out waiting to release the reset fixture.");
                    }
                }));
            if (!resetStarted.Wait(TimeSpan.FromSeconds(2)) && reset.IsCompleted)
            {
                await reset;
            }
            Assert.True(resetStarted.IsSet);
            var stopped = coordinator.GetStatus();
            Assert.Equal(HostRuntimeDesiredState.Stopped, stopped.DesiredState);
            Assert.Null(stopped.ActiveOperationId);

            var start = coordinator.SubmitStartAsync(
                new HostLifecycleRequest(Guid.NewGuid(), stopped.DeploymentGeneration));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(start.IsCompleted);

            releaseReset.Set();
            var resetResult = await reset.WaitAsync(TimeSpan.FromSeconds(2));
            var startSubmission = await start.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(resetResult.Result.Success);
            Assert.NotNull(resetResult.Result.ReceiptRelativePath);
            Assert.Equal(
                RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(runtimeRoot),
                resetResult.RootIdentity);
            Assert.Equal(HostOperationKinds.RuntimeStart, startSubmission.Operation.Kind);
        }
        finally
        {
            releaseReset.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWorkerCoordinator_FailedResetFencesStartupUntilSuccessfulRetry()
    {
        var root = CreateTempDirectory();
        var hostRoot = Path.Combine(root, "host");
        var runtimeRoot = Path.Combine(root, "runtime");
        var preservedRoot = Path.Combine(root, "preserved-runtime");
        try
        {
            RuntimeLocalState.EnsureInitialized(runtimeRoot);
            using var store = new HostLifecycleStore(hostRoot);
            await using (var coordinator = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             Path.Combine(root, "connection", "worker.json"),
                             TimeSpan.FromSeconds(5),
                             store,
                             isRuntimeLeaseAvailable: static () => true,
                             runtimeStateRoot: runtimeRoot))
            {
                await Assert.ThrowsAsync<AggregateException>(() => coordinator.ResetRuntimeStateAsync(
                    startupCacheOnly: false,
                    afterStarted: () =>
                    {
                        Directory.Move(runtimeRoot, preservedRoot);
                        Directory.CreateDirectory(runtimeRoot);
                    }));

                var status = coordinator.GetStatus();
                var conflict = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                    coordinator.SubmitStartAsync(
                        new HostLifecycleRequest(Guid.NewGuid(), status.DeploymentGeneration)));
                Assert.Equal("host.runtime-reset-required", conflict.Code);
                Assert.True(store.LoadOrCreate().ResetRequired);
            }

            await using (var recovered = new RuntimeWorkerCoordinator(
                             runtimeHostPath: null,
                             Path.Combine(root, "connection", "worker.json"),
                             TimeSpan.FromSeconds(5),
                             store,
                             isRuntimeLeaseAvailable: static () => true,
                             runtimeStateRoot: runtimeRoot))
            {
                var status = recovered.GetStatus();
                var conflict = await Assert.ThrowsAsync<HostLifecycleConflictException>(() =>
                    recovered.SubmitRestartAsync(
                        new HostLifecycleRequest(Guid.NewGuid(), status.DeploymentGeneration)));
                Assert.Equal("host.runtime-reset-required", conflict.Code);

                var reset = await recovered.ResetRuntimeStateAsync(startupCacheOnly: false);

                Assert.True(reset.Result.Success);
                Assert.False(store.LoadOrCreate().ResetRequired);
            }
        }
        finally
        {
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
            await Eventually.ObserveAsync(
                _ => ValueTask.FromResult(coordinator.GetStatus()),
                static status => status.State == HostRuntimeState.CrashLoop,
                TimeSpan.FromSeconds(3),
                describe: (status, error) => $"status={status}; error={error?.Message}");

            Assert.Equal(HostRuntimeState.CrashLoop, coordinator.GetStatus().State);
            Assert.Equal(HostRuntimeDesiredState.Running, coordinator.GetStatus().DesiredState);
            Assert.Equal("host.worker-crash-loop", coordinator.GetStatus().FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeWorkerCoordinator_ReportsTypedPreListenRuntimeFailure(bool storageActivationFailure)
    {
        var root = CreateTempDirectory();
        var runtimeStateRoot = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtimeStateRoot);
        var evidencePath = Path.Combine(runtimeStateRoot, "unexpected-state");
        if (storageActivationFailure)
        {
            RuntimeLocalState.EnsureInitialized(runtimeStateRoot);
            var backup = Path.Combine(runtimeStateRoot, "package-data", "test.package",
                ".workspace.sunder-activation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            evidencePath = Path.Combine(backup, "preserved.txt");
        }
        await File.WriteAllTextAsync(evidencePath, "preserve");
        try
        {
            using var store = new HostLifecycleStore(Path.Combine(root, "host"));
            await using var coordinator = new RuntimeWorkerCoordinator(
                typeof(RuntimeHostStartupOptions).Assembly.Location,
                Path.Combine(root, "connection", "worker.json"),
                TimeSpan.FromSeconds(30),
                store,
                getRecoveryDelay: static _ => TimeSpan.FromDays(1),
                runtimeStateRoot: runtimeStateRoot);

            var exception = await Assert.ThrowsAsync<RuntimeWorkerStartupException>(() =>
                coordinator.ReconcileStartupAsync(CancellationToken.None));

            Assert.Equal(storageActivationFailure
                ? "runtime.storage-activation-recovery-required" : "runtime.v1.internal", exception.Code);
            if (storageActivationFailure)
            {
                Assert.Contains("test.package", exception.Message, StringComparison.Ordinal);
                Assert.Contains("Explicit storage recovery", exception.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal("The Runtime V1 state root has no schema metadata and cannot be reset safely.",
                    exception.Message);
            }
            Assert.Equal(exception.Code, coordinator.GetStatus().FailureCode);
            Assert.Equal(exception.Message, coordinator.GetStatus().FailureMessage);
            Assert.Equal("preserve", await File.ReadAllTextAsync(evidencePath));
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
        var leaseChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                isRuntimeLeaseAvailable: () =>
                {
                    leaseChecked.TrySetResult();
                    return Volatile.Read(ref leaseAvailable) == 1;
                });
            var reconciliation = coordinator.ReconcileStartupAsync(CancellationToken.None);
            await leaseChecked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var submission = coordinator.SubmitStopAsync(new HostLifecycleRequest(Guid.NewGuid(), 0));
            await Eventually.AssertPendingAsync(
                submission,
                () => $"reconciliation={reconciliation.Status}; status={coordinator.GetStatus()}");
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

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Medium")]
    public async Task RuntimeWorkerCoordinator_PayloadReplacementPersistenceFaultRetainsPreviousStateAndAbandonsCandidate()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-runtime-payload-persistence-fault");
        var root = scope.RootPath;
        var hostRoot = Path.Combine(root, "host");
        var runtimeRoot = Path.Combine(root, "runtime");
        var connectionPath = Path.Combine(hostRoot, "connection", "runtime-worker.json");
        var candidateDirectory = Path.Combine(root, "payloads", "candidate");
        var candidatePath = Path.Combine(candidateDirectory, "Sunder.Runtime.Host.dll");
        Directory.CreateDirectory(candidateDirectory);
        await File.WriteAllTextAsync(candidatePath, "candidate-runtime-payload");
        var leaseChecks = 0;

        using var store = new HostLifecycleStore(hostRoot);
        store.Save(HostLifecyclePersistentState.Initial with
        {
            DesiredState = HostRuntimeDesiredState.Stopped,
        });
        await using var coordinator = new RuntimeWorkerCoordinator(
            candidatePath,
            connectionPath,
            TimeSpan.FromSeconds(2),
            store,
            isRuntimeLeaseAvailable: () =>
            {
                Interlocked.Increment(ref leaseChecks);
                return true;
            },
            getRecoveryDelay: static _ => TimeSpan.FromDays(1),
            runtimeStateRoot: runtimeRoot);

        var lifecyclePath = Path.Combine(hostRoot, "runtime", "v1", "lifecycle.json");
        var previousDocument = await File.ReadAllBytesAsync(lifecyclePath);
        File.Delete(lifecyclePath);
        Directory.CreateDirectory(lifecyclePath);
        Exception? persistenceFailure;
        try
        {
            persistenceFailure = await Record.ExceptionAsync(() => coordinator.SubmitStartAsync(
                new HostLifecycleRequest(Guid.NewGuid(), 0)));
        }
        finally
        {
            Directory.Delete(lifecyclePath);
            await File.WriteAllBytesAsync(lifecyclePath, previousDocument);
        }

        Assert.True(
            persistenceFailure is IOException or UnauthorizedAccessException,
            $"Expected an atomic lifecycle persistence failure, but observed {persistenceFailure?.GetType().Name ?? "no failure"}: {persistenceFailure?.Message}");
        Assert.Equal(0, Volatile.Read(ref leaseChecks));
        Assert.Equal(HostRuntimeDesiredState.Stopped, coordinator.GetStatus().DesiredState);
        Assert.Equal(HostRuntimeState.Stopped, coordinator.GetStatus().State);
        Assert.Equal(0, coordinator.GetStatus().DeploymentGeneration);
        Assert.Null(coordinator.GetStatus().ActiveOperationId);
        var durable = store.LoadOrCreate();
        Assert.Equal(HostRuntimeDesiredState.Stopped, durable.DesiredState);
        Assert.Empty(durable.Operations);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(lifecyclePath)!, ".lifecycle.*.tmp"));
        Assert.False(File.Exists(connectionPath));
        Assert.True(RuntimeLocalState.IsLeaseAvailable(runtimeRoot));

        Directory.Delete(candidateDirectory, recursive: true);
        Assert.False(Directory.Exists(candidateDirectory));
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "Process")]
    [Trait("Size", "Large")]
    public async Task RuntimeWorkerCoordinator_PayloadReplacementLaunchFaultAdvancesCandidateWithoutDuplicateOwner()
    {
        await RunPayloadReplacementCompensationOracleAsync(mutateDurableState: null);
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "Process")]
    [Trait("Size", "Large")]
    public async Task RuntimeWorkerCoordinator_PayloadReplacementOracleRejectsLostDesiredIntentMutation()
    {
        var exception = await Assert.ThrowsAsync<PayloadReplacementOracleException>(() =>
            RunPayloadReplacementCompensationOracleAsync(static state => state with
            {
                DesiredState = HostRuntimeDesiredState.Stopped,
            }));

        Assert.Contains("lost durable Running intent", exception.Message, StringComparison.Ordinal);
    }

    private static async Task RunPayloadReplacementCompensationOracleAsync(
        Func<HostLifecyclePersistentState, HostLifecyclePersistentState>? mutateDurableState)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-runtime-payload-compensation");
        var root = scope.RootPath;
        var hostRoot = Path.Combine(root, "host");
        var runtimeRoot = Path.Combine(root, "runtime");
        var connectionPath = Path.Combine(hostRoot, "connection", "runtime-worker.json");
        var candidateDirectory = Path.Combine(root, "payloads", "candidate");
        var sourceRuntimeAssembly = typeof(RuntimeHostStartupOptions).Assembly.Location;
        CopyDirectory(Path.GetDirectoryName(sourceRuntimeAssembly)!, candidateDirectory);
        var candidatePath = Path.Combine(candidateDirectory, Path.GetFileName(sourceRuntimeAssembly));
        var validCandidateBytes = await File.ReadAllBytesAsync(candidatePath);
        await File.WriteAllTextAsync(candidatePath, "seeded invalid managed Runtime payload");
        var leaseBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = 0;

        using (var store = new HostLifecycleStore(hostRoot))
        {
            store.Save(HostLifecyclePersistentState.Initial with
            {
                DesiredState = HostRuntimeDesiredState.Stopped,
            });
            await using var coordinator = new RuntimeWorkerCoordinator(
                candidatePath,
                connectionPath,
                TimeSpan.FromSeconds(5),
                store,
                isRuntimeLeaseAvailable: () =>
                {
                    leaseBoundary.TrySetResult();
                    return Volatile.Read(ref releaseLease) == 1;
                },
                getRecoveryDelay: static _ => TimeSpan.FromDays(1),
                runtimeStateRoot: runtimeRoot);

            var submission = await coordinator.SubmitStartAsync(new HostLifecycleRequest(Guid.NewGuid(), 0));
            await leaseBoundary.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var persistedBeforeLaunch = store.LoadOrCreate();
            Assert.Equal(HostRuntimeDesiredState.Running, persistedBeforeLaunch.DesiredState);
            Assert.Equal(1, persistedBeforeLaunch.DeploymentGeneration);
            Assert.Equal(submission.Operation.OperationId, persistedBeforeLaunch.ActiveOperationId);
            Assert.False(File.Exists(connectionPath));
            Assert.Null(coordinator.GetWorkerConnection());

            Volatile.Write(ref releaseLease, 1);
            var failed = await coordinator.WaitForOperationAsync(submission.Operation.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HostOperationState.Failed, failed.State);
            Assert.Equal(HostRuntimeDesiredState.Running, coordinator.GetStatus().DesiredState);
            Assert.Equal(HostRuntimeState.Failed, coordinator.GetStatus().State);
            Assert.Null(coordinator.GetStatus().ActiveOperationId);
            Assert.Null(coordinator.GetWorkerConnection());
            Assert.False(File.Exists(connectionPath));
            Assert.True(RuntimeLocalState.IsLeaseAvailable(runtimeRoot));
            var persistedAfterFailure = store.LoadOrCreate();
            Assert.Equal(HostRuntimeDesiredState.Running, persistedAfterFailure.DesiredState);
            Assert.Null(persistedAfterFailure.ActiveOperationId);
        }

        await File.WriteAllBytesAsync(candidatePath, validCandidateBytes);
        if (mutateDurableState is not null)
        {
            using var mutationStore = new HostLifecycleStore(hostRoot);
            mutationStore.Save(mutateDurableState(mutationStore.LoadOrCreate()));
        }

        using (var store = new HostLifecycleStore(hostRoot))
        await using (var coordinator = new RuntimeWorkerCoordinator(
                         candidatePath,
                         connectionPath,
                         TimeSpan.FromSeconds(30),
                         store,
                         getRecoveryDelay: static _ => TimeSpan.FromDays(1),
                         runtimeStateRoot: runtimeRoot))
        {
            await coordinator.ReconcileStartupAsync(CancellationToken.None);
            var status = coordinator.GetStatus();
            var durable = store.LoadOrCreate();
            AssertCoherentAdvancedPayload(status, durable, coordinator.GetWorkerConnection());
            Assert.False(RuntimeLocalState.IsLeaseAvailable(runtimeRoot));

            var competitorRoot = Path.Combine(root, "competitor-host");
            using var competitorStore = new HostLifecycleStore(competitorRoot);
            await using var competitor = new RuntimeWorkerCoordinator(
                candidatePath,
                Path.Combine(competitorRoot, "connection", "runtime-worker.json"),
                TimeSpan.FromMilliseconds(350),
                competitorStore,
                getRecoveryDelay: static _ => TimeSpan.FromDays(1),
                runtimeStateRoot: runtimeRoot);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                competitor.ReconcileStartupAsync(CancellationToken.None));
            Assert.Null(competitor.GetWorkerConnection());
            Assert.Equal("host.runtime-lease-timeout", competitor.GetStatus().FailureCode);
            Assert.NotNull(coordinator.GetWorkerConnection());

            await coordinator.StopForSupervisorShutdownAsync();
            Assert.True(RuntimeLocalState.IsLeaseAvailable(runtimeRoot));
            Assert.False(File.Exists(connectionPath));
            await competitor.StopForSupervisorShutdownAsync();
        }

        Directory.Delete(candidateDirectory, recursive: true);
        Assert.False(Directory.Exists(candidateDirectory));
        Assert.True(RuntimeLocalState.IsLeaseAvailable(runtimeRoot));
    }

    private static void AssertCoherentAdvancedPayload(
        HostRuntimeStatus status,
        HostLifecyclePersistentState durable,
        RuntimeWorkerConnection? connection)
    {
        if (durable.DesiredState != HostRuntimeDesiredState.Running
            || status.DesiredState != HostRuntimeDesiredState.Running)
        {
            throw new PayloadReplacementOracleException(
                $"Payload replacement lost durable Running intent. status={status}; durable={durable}");
        }
        if (status.State != HostRuntimeState.Ready
            || status.ActiveOperationId is not null
            || durable.ActiveOperationId is not null
            || connection is null)
        {
            throw new PayloadReplacementOracleException(
                $"Payload replacement did not advance to one coherent live owner. status={status}; durable={durable}; connection={connection}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private sealed class PayloadReplacementOracleException(string message) : Exception(message);

    private static async Task WaitForOperationStateAsync(
        RuntimeWorkerCoordinator coordinator,
        string operationId,
        HostOperationState expectedState)
    {
        await Eventually.ObserveAsync(
            _ => ValueTask.FromResult(
                coordinator.TryGetOperation(operationId, out var operation)
                    ? operation
                    : null),
            operation => operation?.State == expectedState,
            TimeSpan.FromSeconds(2),
            describe: (operation, error) =>
                $"operationId={operationId}; expected={expectedState}; actual={operation?.State.ToString() ?? "<missing>"}; status={coordinator.GetStatus()}; error={error?.Message}");
    }
}
