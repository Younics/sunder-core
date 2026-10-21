using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Host.Client;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppRuntimeProcessTransportTests
{
    [Fact]
    public void PrivateDiscoveryPreservesCustomWorkerPathWithoutExposingItInHttpDtos()
    {
        using var root = TemporaryDirectoryScope.Create("sunder-app-ipc-discovery");
        var gatewayPath = root.GetPath("external", "host.json");
        var workerPath = root.GetPath("private", "runtime-worker.json");
        var gateway = Gateway() with { WorkerConnectionInfoPath = workerPath };
        RuntimeConnectionInfoStore.Save(gateway, gatewayPath);
        Assert.Null(HostConnectionInfoStore.LoadLocalRuntimeWorkerFor(RuntimeConnectionInfoStore.Load(gatewayPath)!));
        var worker = Worker();
        RuntimeConnectionInfoStore.Save(worker, workerPath);
        var loaded = HostConnectionInfoStore.LoadLocalRuntimeWorkerFor(RuntimeConnectionInfoStore.Load(gatewayPath)!);
        Assert.Equal(worker, loaded);
        Assert.Equal(worker.IpcEndpoint, loaded!.IpcEndpoint);
        Assert.DoesNotContain("ipcEndpoint", JsonSerializer.Serialize(loaded), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workerConnectionInfoPath", JsonSerializer.Serialize(gateway), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => RuntimeConnectionInfoStore.LoadFor(new Uri("http://127.0.0.1:5276/"), gatewayPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPrivateIpcDoesNotFallBackToTheTcpGateway(bool policyChannel)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var gateway = new RuntimeClientTransport(Gateway, handler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => null);
        var selected = policyChannel ? direct.PolicyTransport : direct.Transport;
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected.ProbeHandshakeAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AppRpcUsesDirectIpcAndThePrivateWorkerCredential()
    {
        var handshake = Handshake();
        using var gatewayHandler = new Handler(_ => Json(handshake));
        using var workerHandler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/handshake" ? Json(handshake) : Json(new { ok = true }));
        using var gateway = new RuntimeClientTransport(Gateway, gatewayHandler);
        var worker = Worker();
        using var direct = new AppRuntimeProcessTransport(gateway, _ => worker, endpoint =>
        {
            Assert.Equal(worker.IpcEndpoint, endpoint);
            return workerHandler;
        });
        Assert.Equal(handshake.RuntimeInstanceId, (await direct.HandshakeAsync(default)).RuntimeInstanceId);
        using var result = await direct.Transport.GetAsync(direct.Transport.CreateRequestUri("rpc/app/probe"));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.All(gatewayHandler.Requests, request => Assert.Equal(("/api/handshake", "Bearer gateway-secret"), request));
        Assert.Contains(("/api/v1/rpc/app/probe", "Bearer worker-secret"), workerHandler.Requests);
        Assert.All(workerHandler.Requests, request => Assert.Equal("Bearer worker-secret", request.Authorization));
    }

    [Fact]
    public async Task DirectAndGatewayRuntimeInstancesMustAgreeBeforeLoading()
    {
        using var gatewayHandler = new Handler(_ => Json(Handshake()));
        using var workerHandler = new Handler(_ => Json(Handshake()));
        using var gateway = new RuntimeClientTransport(Gateway, gatewayHandler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => Worker(), _ => workerHandler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => direct.HandshakeAsync(default));
    }

    [Fact]
    public async Task ChangedHostIsObservedEvenBeforeItsPrivateConnectionIsAvailable()
    {
        var handshake = Handshake();
        RuntimeConnectionInfo? worker = Worker();
        using var gatewayHandler = new Handler(_ => Json(handshake));
        using var workerHandler = new Handler(_ => Json(handshake));
        using var gateway = new RuntimeClientTransport(Gateway, gatewayHandler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => worker, _ => workerHandler);
        await direct.HandshakeAsync(default);
        var previousRequests = workerHandler.Requests.Count;
        handshake = Handshake();
        worker = null;
        Assert.Equal(handshake.RuntimeInstanceId, (await direct.HandshakeAsync(default)).RuntimeInstanceId);
        Assert.Equal(previousRequests, workerHandler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerReplacementBetweenCredentialSelectionAndSendFailsClosed(bool policyChannel)
    {
        using var gateway = new RuntimeClientTransport(Gateway);
        var reads = 0;
        var handlers = 0;
        using var direct = new AppRuntimeProcessTransport(gateway,
            _ => ++reads == 1 ? Worker() : Worker() with { BearerToken = "replacement" },
            _ => { handlers++; return new Handler(_ => Json(Handshake())); });
        var selected = policyChannel ? direct.PolicyTransport : direct.Transport;
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected.ProbeHandshakeAsync());
        Assert.Equal(0, handlers);
    }

    [Fact]
    public async Task PackageRpcNegotiationCannotQueuePolicyHeartbeatsBehindLongPackageWork()
    {
        var handshake = Handshake();
        var negotiationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNegotiation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpcHandshakes = 0;
        using var rpcHandler = new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/handshake")
            {
                if (Interlocked.Increment(ref rpcHandshakes) == 1)
                {
                    negotiationStarted.TrySetResult();
                    await releaseNegotiation.Task.WaitAsync(token).ConfigureAwait(false);
                }
                return Json(handshake);
            }
            return Json(new { ok = true });
        });
        var session = new RuntimeAppPolicySessionRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid(), handshake.RuntimeInstanceId);
        var status = new RuntimeAppPolicySessionStatus(session.SessionId, RuntimeAppPolicySessionStates.Active, null, false, []);
        using var policyHandler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/handshake"
            ? Json(handshake) : Json(status));
        using var gatewayHandler = new Handler(_ => Json(handshake));
        using var gateway = new RuntimeClientTransport(Gateway, gatewayHandler);
        var handlers = 0;
        using var direct = new AppRuntimeProcessTransport(gateway, _ => Worker(),
            _ => Interlocked.Increment(ref handlers) == 1 ? rpcHandler : policyHandler);
        var packageCall = direct.Transport.GetAsync(direct.Transport.CreateRequestUri("rpc/app/held"));
        try
        {
            await negotiationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.Equal(handshake.RuntimeInstanceId, (await direct.HandshakeAsync(deadline.Token)).RuntimeInstanceId);
            using var policy = new RuntimeManagementClient(direct.PolicyTransport);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var observed = await policy.HeartbeatAppPolicySessionAsync(session, deadline.Token);
                Assert.Equal(session.SessionId, observed.SessionId);
                Assert.False(observed.ShutdownRequired);
            }

            Assert.False(packageCall.IsCompleted);
            Assert.Equal(2, handlers);
            Assert.Equal(3, policyHandler.Requests.Count(request => request.Path.EndsWith("/heartbeat", StringComparison.Ordinal)));
            Assert.All(policyHandler.Requests, request => Assert.Equal("Bearer worker-secret", request.Authorization));
        }
        finally
        {
            releaseNegotiation.TrySetResult();
            using var response = await packageCall.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false, "gateway-handshake-failed")]
    [InlineData(true, "private-handshake-failed")]
    public async Task HandshakeFailureRetainsTheFailedTransportPhase(bool privateFailure, string expectedPhase)
    {
        var handshake = Handshake();
        using var gatewayHandler = new Handler(_ => privateFailure ? Json(handshake) : throw new HttpRequestException("gateway unavailable"));
        using var workerHandler = new Handler(_ => throw new HttpRequestException("private IPC unavailable"));
        using var gateway = new RuntimeClientTransport(Gateway, gatewayHandler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => Worker(), _ => workerHandler);

        await Assert.ThrowsAsync<HttpRequestException>(() => direct.HandshakeAsync(default));

        Assert.Equal(expectedPhase, direct.HandshakePhase);
    }

    [Fact]
    public void DedicatedHeartbeatAlsoSupportsAnIpcGatewayWithoutAnAsyncOrTcpFallback()
    {
        var handshake = Handshake();
        var session = new RuntimeAppPolicySessionRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid(), handshake.RuntimeInstanceId);
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/handshake" ? Json(handshake)
            : Json(new RuntimeAppPolicySessionStatus(session.SessionId, "active", null, false, [])));
        var worker = Worker();
        using var gateway = new RuntimeClientTransport(() => worker, handler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => worker, _ => handler);
        Assert.Equal(handshake.RuntimeInstanceId, direct.HeartbeatHandshake(default).RuntimeInstanceId);
        Assert.Equal(session.SessionId, direct.Heartbeat(session, default).SessionId);
        Assert.Equal(3, handler.SynchronousSends);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer worker-secret", request.Authorization));
    }

    private static RuntimeConnectionInfo Gateway() => new(new Uri("http://127.0.0.1:5275/"), "gateway-secret");
    private static RuntimeConnectionInfo Worker() => new(RuntimeIpcEndpoint.LogicalRuntimeUrl, "worker-secret")
    {
        IpcEndpoint = OperatingSystem.IsWindows() ? new(RuntimeIpcTransportKind.NamedPipe, "sunder-app-test")
            : new(RuntimeIpcTransportKind.UnixSocket, "/tmp/sunder-app-test.sock"),
    };
    private static RuntimeHandshakeResponse Handshake() => new(RuntimeProtocol.Identity, 1, 1, 1, Guid.NewGuid(),
        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1], new("Sunder.Runtime.Host", "1.0.0", "1.0.0"));
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<(string Path, string? Authorization)> Requests { get; } = [];
        internal int SynchronousSends;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            SynchronousSends++;
            return Respond(request);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(Respond(request));
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            var result = response(request);
            result.RequestMessage = request;
            return result;
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var result = await response(request, token).ConfigureAwait(false);
            result.RequestMessage = request;
            return result;
        }
    }
}
