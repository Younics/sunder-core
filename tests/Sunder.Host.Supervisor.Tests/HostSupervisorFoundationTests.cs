using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using Microsoft.AspNetCore.Http;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed class HostSupervisorFoundationTests
{
    [Fact]
    public void HostStartupOptions_PreservesPerUserDefaults()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = HostStartupOptions.Parse([], static _ => null, root);

            Assert.Null(options.RuntimeHostPath);
            Assert.Equal(TimeSpan.FromSeconds(30), options.WorkerStartupTimeout);
            Assert.Null(options.HostStateRoot);
            Assert.Null(options.RuntimeStateRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostStartupOptions_NormalizesExplicitDebugPaths()
    {
        var root = CreateTempDirectory();
        var runtimeHost = Path.Combine(root, "Sunder.Runtime.Host.dll");
        File.WriteAllBytes(runtimeHost, []);
        try
        {
            var options = HostStartupOptions.Parse(
                [
                    "--runtime-host-path", runtimeHost,
                    "--host-state-root", Path.Combine(root, "host"),
                    "--runtime-state-root", Path.Combine(root, "runtime"),
                    "--worker-startup-timeout-seconds", "12.5",
                ],
                static _ => null,
                root);

            Assert.Equal(runtimeHost, options.RuntimeHostPath);
            Assert.Equal(Path.Combine(root, "host"), options.HostStateRoot);
            Assert.Equal(Path.Combine(root, "runtime"), options.RuntimeStateRoot);
            Assert.Equal(TimeSpan.FromSeconds(12.5), options.WorkerStartupTimeout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostBearerTokenValidator_RequiresMatchingBearerCredential()
    {
        var validator = new HostBearerTokenValidator("expected-token");

        Assert.True(validator.IsValid("Bearer expected-token"));
        Assert.False(validator.IsValid(null));
        Assert.False(validator.IsValid("Basic expected-token"));
        Assert.False(validator.IsValid("Bearer wrong-token"));
    }

    [Fact]
    public void HostRuntimeResetChallenge_IsOneTimeAndRejectsWrongConfirmation()
    {
        var challenges = new HostRuntimeResetChallengeService();
        var first = challenges.Create();

        Assert.False(challenges.TryConsume("wrong"));
        Assert.False(challenges.TryConsume(first.Challenge));

        var second = challenges.Create();
        Assert.True(challenges.TryConsume(second.Challenge));
        Assert.False(challenges.TryConsume(second.Challenge));
    }

    [Fact]
    public void ListenUrlValidator_AcceptsLoopbackAndRejectsRemoteHttp()
    {
        Assert.Equal(
            "http://127.0.0.1:5275",
            HostListenUrlValidator.ParseAndValidateLocal("http://127.0.0.1:5275"));
        Assert.Throws<InvalidOperationException>(() =>
            HostListenUrlValidator.ParseAndValidateLocal("http://192.0.2.1:5275"));
        Assert.Throws<InvalidOperationException>(() =>
            HostListenUrlValidator.ParseAndValidateLocal("http://127.0.0.1:5275;http://127.0.0.1:5276"));
    }

    [Fact]
    public void IdentityStore_PersistsStableHostIdAndFailsClosedOnCorruption()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new HostIdentityStore(root);
            var first = store.LoadOrCreateHostId();

            Assert.NotEqual(Guid.Empty, first);
            Assert.Equal(first, new HostIdentityStore(root).LoadOrCreateHostId());

            File.WriteAllText(Path.Combine(root, "identity.json"), "{}");
            Assert.Throws<InvalidDataException>(() => new HostIdentityStore(root).LoadOrCreateHostId());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            "1.2.3",
            "1.2.2",
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
                Assert.Equal(expected.ActiveVersion, actual.ActiveVersion);
                Assert.Equal(expected.PreviousVersion, actual.PreviousVersion);
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
    public void RuntimeWorkerEndpointLease_CreatesPrivatePlatformEndpointAndCleansUp()
    {
        string? directoryPath = null;
        string? socketPath = null;
        using (var endpointLease = RuntimeWorkerEndpointLease.Create())
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(RuntimeIpcTransportKind.NamedPipe, endpointLease.Endpoint.Kind);
                Assert.StartsWith("sunder-runtime-", endpointLease.Endpoint.Address, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(RuntimeIpcTransportKind.UnixSocket, endpointLease.Endpoint.Kind);
                socketPath = endpointLease.Endpoint.Address;
                directoryPath = Path.GetDirectoryName(socketPath);
                Assert.NotNull(directoryPath);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directoryPath));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(socketPath));
            Assert.False(Directory.Exists(directoryPath));
        }
    }

    [Fact]
    public void RuntimeIpcEndpoint_RoundTripsAndRejectsInvalidAddresses()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-test")
            : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "/tmp/sunder-runtime-test.sock");

        Assert.Equal(endpoint, RuntimeIpcEndpoint.Parse(endpoint.ToEnvironmentValue()));
        endpoint.EnsureSupportedPlatform();
        Assert.Throws<FormatException>(() => RuntimeIpcEndpoint.Parse("tcp:127.0.0.1:1234"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "folder/pipe"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "relative.sock"));

        using var handler = Assert.IsType<SocketsHttpHandler>(
            RuntimeIpcHttpMessageHandlerFactory.Create(endpoint));
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
    }

    [Fact]
    public async Task RuntimeIpcHttpMessageHandler_ConnectsToLogicalRuntimeOrigin()
    {
        using var endpointLease = RuntimeWorkerEndpointLease.Create();
        IDisposable listener;
        Task server;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeServerStream(
                endpointLease.Endpoint.Address,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            listener = pipe;
            server = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync();
                await ServeSingleHttpRequestAsync(pipe);
            });
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(endpointLease.Endpoint.Address));
            socket.Listen(1);
            listener = socket;
            server = Task.Run(async () =>
            {
                using var accepted = await socket.AcceptAsync();
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                await ServeSingleHttpRequestAsync(stream);
            });
        }

        using (listener)
        using (var client = new HttpClient(RuntimeIpcHttpMessageHandlerFactory.Create(endpointLease.Endpoint))
        {
            Timeout = TimeSpan.FromSeconds(5),
        })
        {
            Assert.Equal("ok", await client.GetStringAsync(RuntimeIpcEndpoint.LogicalRuntimeUrl));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
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
                Assert.Equal(HostRuntimeState.Stopped, retry.Status.State);
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
                    null,
                    null,
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

    [Fact]
    public async Task Gateway_ForwardsStreamingBodyWithPrivateWorkerCredential()
    {
        var workerConnection = CreateWorkerConnection(
            new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5276/"), "worker-secret"));
        HttpRequestMessage? forwarded = null;
        byte[]? forwardedBody = null;
        using var gateway = new RuntimeGateway(
            new StubConnectionSource(workerConnection),
            new RecordingHandler(async request =>
            {
                forwarded = request;
                forwardedBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsByteArrayAsync();
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("forwarded", Encoding.UTF8, "text/plain"),
                };
            }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/packages/demo/operations/run";
        context.Request.QueryString = new QueryString("?mode=test");
        context.Request.Headers.Authorization = "Bearer external-secret";
        context.Request.Headers["X-Sunder-Client-Id"] = "spoofed";
        context.Request.ContentType = "application/octet-stream";
        context.Request.ContentLength = 4;
        context.Request.Body = new MemoryStream([1, 2, 3, 4]);
        context.Response.Body = new MemoryStream();

        await gateway.ForwardAsync(context);

        Assert.NotNull(forwarded);
        Assert.Equal("http://127.0.0.1:5276/api/v1/packages/demo/operations/run?mode=test", forwarded.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer worker-secret", forwarded.Headers.Authorization!.ToString());
        Assert.False(forwarded.Headers.Contains("X-Sunder-Client-Id"));
        Assert.Equal([1, 2, 3, 4], forwardedBody);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("forwarded", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Gateway_ReturnsTypedUnavailableProblemWithoutWorker()
    {
        using var gateway = new RuntimeGateway(new StubConnectionSource(null));
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/handshake";
        context.Response.Body = new MemoryStream();

        await gateway.ForwardAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("host.runtime-unavailable", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_RejectsStaleEpochWithoutReplacingCurrentClient()
    {
        var first = CreateWorkerConnection(
            new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "first-token")) with
        {
            WorkerEpoch = 1,
        };
        var second = CreateWorkerConnection(
            new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "second-token")) with
        {
            WorkerEpoch = 2,
            Endpoint = OperatingSystem.IsWindows()
                ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-second")
                : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "/tmp/sunder-runtime-second.sock"),
        };
        var source = new SequencedConnectionSource(second);
        var createdEpochs = new List<string>();
        using var gateway = new RuntimeGateway(
            source,
            handlerFactory: endpoint =>
            {
                createdEpochs.Add(endpoint.Address);
                return new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok"),
                }));
            });

        var current = CreateGatewayContext();
        await gateway.ForwardAsync(current);
        source.Enqueue(first);
        var stale = CreateGatewayContext();
        await gateway.ForwardAsync(stale);
        var currentAgain = CreateGatewayContext();
        await gateway.ForwardAsync(currentAgain);

        Assert.Equal(StatusCodes.Status200OK, current.Response.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, stale.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, currentAgain.Response.StatusCode);
        Assert.Equal([second.Endpoint.Address], createdEpochs);
    }

    [Fact]
    public async Task Gateway_ClearsBackendHeadersBeforeWritingUnavailableProblem()
    {
        var worker = CreateWorkerConnection(
            new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "worker-token"));
        using var gateway = new RuntimeGateway(
            new StubConnectionSource(worker),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowingHttpContent(),
            })));
        var context = CreateGatewayContext();

        await gateway.ForwardAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Null(context.Response.ContentLength);
        context.Response.Body.Position = 0;
        Assert.Contains(
            "host.runtime-unavailable",
            await new StreamReader(context.Response.Body).ReadToEndAsync(),
            StringComparison.Ordinal);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private static async Task ServeSingleHttpRequestAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
        await stream.FlushAsync();
    }

    private static RuntimeWorkerConnection CreateWorkerConnection(RuntimeConnectionInfo connection)
        => new(
            connection,
            OperatingSystem.IsWindows()
                ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-test")
                : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "/tmp/sunder-runtime-test.sock"),
            WorkerEpoch: 1);

    private static DefaultHttpContext CreateGatewayContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/handshake";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class StubConnectionSource(RuntimeWorkerConnection? connection) : IRuntimeWorkerConnectionSource
    {
        public RuntimeWorkerConnection? GetWorkerConnection() => connection;
    }

    private sealed class SequencedConnectionSource(RuntimeWorkerConnection current) : IRuntimeWorkerConnectionSource
    {
        private readonly Queue<RuntimeWorkerConnection> _queued = [];

        public void Enqueue(RuntimeWorkerConnection connection) => _queued.Enqueue(connection);

        public RuntimeWorkerConnection? GetWorkerConnection()
            => _queued.TryDequeue(out var connection) ? connection : current;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request);
    }

    private sealed class ThrowingHttpContent : HttpContent
    {
        public ThrowingHttpContent() => Headers.ContentLength = 32;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("backend stream failed"));

        protected override bool TryComputeLength(out long length)
        {
            length = 32;
            return true;
        }
    }
}
