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

    [Fact]
    public async Task MissingPrivateIpcDoesNotFallBackToTheTcpGateway()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var gateway = new RuntimeClientTransport(Gateway, handler);
        using var direct = new AppRuntimeProcessTransport(gateway, _ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => direct.Transport.ProbeHandshakeAsync());
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

    [Fact]
    public async Task WorkerReplacementBetweenCredentialSelectionAndSendFailsClosed()
    {
        using var gateway = new RuntimeClientTransport(Gateway);
        var reads = 0;
        var handlers = 0;
        using var direct = new AppRuntimeProcessTransport(gateway,
            _ => ++reads == 1 ? Worker() : Worker() with { BearerToken = "replacement" },
            _ => { handlers++; return new Handler(_ => Json(Handshake())); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => direct.Transport.ProbeHandshakeAsync());
        Assert.Equal(0, handlers);
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
