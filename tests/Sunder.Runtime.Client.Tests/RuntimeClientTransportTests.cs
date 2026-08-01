using System.Net;
using System.Net.Http.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class RuntimeClientTransportTests
{
    [Fact]
    public async Task SharedFacades_NegotiateOnceForOneConnectionIdentity()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/system" => Json(new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow)),
            "/api/v1/packages/demo/data/state/key" => Json(new PackageDataValueResponse(true, "value")),
            "/api/v1/packages/demo/callbacks/sessions/session" => Json(new PackageCallbackSessionResponse(
                "demo",
                "callback",
                "session",
                PackageCallbackSessionState.Pending,
                "Pending",
                LaunchUri: null,
                DateTimeOffset.UtcNow.AddMinutes(1))),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(() => connection, handler);
        using var management = new RuntimeManagementClient(transport);
        using var data = new RuntimePackageDataClient(transport);
        using var callbacks = new RuntimePackageCallbackClient(transport);

        await management.GetSystemStatusAsync();
        management.Dispose();
        await data.GetStateAsync("demo", "key");
        await callbacks.GetStatusAsync("demo", "session");

        Assert.Equal(1, handler.Paths.Count(path => path == "/api/handshake"));
        Assert.All(handler.Authorizations, authorization => Assert.Equal("Bearer secret", authorization));
    }

    [Fact]
    public async Task ConnectionIdentityChange_NegotiatesAgainAndNeverSendsBearerToAnotherOrigin()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/system" => Json(new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "first");
        using var transport = new RuntimeClientTransport(() => connection, handler);
        using var management = new RuntimeManagementClient(transport);

        await management.GetSystemStatusAsync();
        connection = connection with { BearerToken = "second" };
        await management.GetSystemStatusAsync();

        Assert.Equal(2, handler.Paths.Count(path => path == "/api/handshake"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.GetAsync(
            new Uri("https://registry.example/media/icon.png"),
            HttpCompletionOption.ResponseHeadersRead));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("media/icon.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeHandshakeAsync_IsUncachedAndReturnsIncompatibleRuntimeDetails()
    {
        var incompatible = CreateHandshake(
            revision: RuntimeProtocol.MinimumSupportedRevision - 1,
            minimum: RuntimeProtocol.MinimumSupportedRevision - 1,
            maximum: RuntimeProtocol.MinimumSupportedRevision - 1);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(incompatible),
            "/api/v1/system/shutdown" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(() => connection, handler);

        var first = await transport.ProbeHandshakeAsync();
        var second = await transport.ProbeHandshakeAsync();

        Assert.Equal(incompatible.ProtocolIdentity, first.ProtocolIdentity);
        Assert.Equal(incompatible.ProtocolRevision, first.ProtocolRevision);
        Assert.Equal(incompatible.RuntimeInstanceId, first.RuntimeInstanceId);
        Assert.Equal(first.RuntimeInstanceId, second.RuntimeInstanceId);
        Assert.Equal(2, handler.Paths.Count(path => path == "/api/handshake"));
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => transport.NegotiateAsync());
        Assert.Equal(3, handler.Paths.Count(path => path == "/api/handshake"));

        await transport.ShutdownWithoutProtocolNegotiationAsync();

        Assert.Equal(3, handler.Paths.Count(path => path == "/api/handshake"));
        Assert.Equal("/api/v1/system/shutdown", handler.Paths[^1]);
    }

    [Fact]
    public async Task ResetControlCalls_BypassRuntimeHandshakeAndRemainAuthenticated()
    {
        RuntimeResetConfirmRequest? confirmation = null;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/system/reset/prepare" => Json(new RuntimeResetChallengeResponse(
                "one-time",
                DateTimeOffset.UtcNow.AddSeconds(30))),
            "/api/v1/system/reset/drain" => Json(new RuntimeResetDrainResponse([])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/system/reset/drain")
            {
                confirmation = request.Content!.ReadFromJsonAsync<RuntimeResetConfirmRequest>()
                    .GetAwaiter()
                    .GetResult();
            }
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var management = new RuntimeManagementClient(() => connection, handler);

        var challenge = await management.PrepareResetAsync();
        await management.DrainForResetAsync(challenge.Challenge);

        Assert.Equal(
            ["/api/v1/system/reset/prepare", "/api/v1/system/reset/drain"],
            handler.Paths);
        Assert.All(handler.Authorizations, authorization => Assert.Equal("Bearer secret", authorization));
        Assert.Equal("one-time", confirmation?.Challenge);
    }

    [Fact]
    public async Task RefreshHandshakeAsync_ReplacesCachedWorkerInstance()
    {
        var first = CreateHandshake();
        var second = CreateHandshake();
        var current = first;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(current),
            "/api/v1/system" => Json(new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(() => connection, handler);
        using var management = new RuntimeManagementClient(transport);

        Assert.Equal(first.RuntimeInstanceId, (await transport.NegotiateAsync()).RuntimeInstanceId);
        current = second;
        Assert.Equal(second.RuntimeInstanceId, (await transport.RefreshHandshakeAsync()).RuntimeInstanceId);
        await management.GetSystemStatusAsync();

        Assert.Equal(2, handler.Paths.Count(path => path == "/api/handshake"));
    }

    [Fact]
    public async Task FailedRefreshHandshakeAsync_RemovesStaleCachedWorker()
    {
        var first = CreateHandshake();
        var incompatible = CreateHandshake(
            revision: RuntimeProtocol.MinimumSupportedRevision - 1,
            minimum: RuntimeProtocol.MinimumSupportedRevision - 1,
            maximum: RuntimeProtocol.MinimumSupportedRevision - 1);
        var replacement = CreateHandshake();
        var current = first;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(current),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(() => connection, handler);

        Assert.Equal(first.RuntimeInstanceId, (await transport.NegotiateAsync()).RuntimeInstanceId);
        current = incompatible;
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => transport.RefreshHandshakeAsync());
        current = replacement;

        Assert.Equal(replacement.RuntimeInstanceId, (await transport.NegotiateAsync()).RuntimeInstanceId);
        Assert.Equal(3, handler.Paths.Count(path => path == "/api/handshake"));
    }

    [Fact]
    public async Task ResponseHeadersRead_StreamReadRetainsTransportDeadline()
    {
        var blockingBody = new CancellationBlockingStream();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(blockingBody),
            },
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(
            () => connection,
            handler,
            new RuntimeClientPolicyOptions { RequestTimeout = TimeSpan.FromSeconds(1) });
        await transport.NegotiateAsync();
        Assert.Equal(["/api/handshake"], handler.Paths);

        using var response = await transport.GetAsync(
            transport.CreateRequestUri("content"),
            HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var read = stream.ReadAsync(new byte[1], CancellationToken.None).AsTask();

        await blockingBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(read, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(["/api/handshake", "/api/v1/content"], handler.Paths);
    }

    [Fact]
    public async Task PackageUiSnapshotRequests_IncludeExactAppRidAndEscapedStageId()
    {
        var handshake = CreateHandshake() with
        {
            SupportedFeatures =
            [
                RuntimeProtocolFeatures.VersionedApiV1,
                RuntimeProtocolFeatures.TargetAwarePackageSnapshotsV1,
            ],
        };
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(handshake),
            "/api/v1/packages/ui-snapshots" => Json(Array.Empty<PackageUiSnapshotDescriptor>()),
            "/api/v1/packages/session/stage/stage%20one/ui-snapshots" => Json(Array.Empty<PackageUiSnapshotDescriptor>()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var management = new RuntimeManagementClient(() => connection, handler);

        await management.GetActivePackageUiSnapshotsAsync("linux-arm64");
        await management.GetStagedPackageUiSnapshotsAsync("stage one", "osx-x64");

        Assert.Equal(
            "/api/v1/packages/ui-snapshots?appRid=linux-arm64",
            handler.PathAndQueries[1]);
        Assert.Equal(
            "/api/v1/packages/session/stage/stage%20one/ui-snapshots?appRid=osx-x64",
            handler.PathAndQueries[2]);
    }

    [Fact]
    public async Task RpcManagement_QueriesCatalogAndManagesPermissions()
    {
        var handshake = CreateHandshake() with
        {
            SupportedFeatures =
            [
                RuntimeProtocolFeatures.VersionedApiV1,
                RuntimeProtocolFeatures.SchemaFirstRpcV1,
                RuntimeProtocolFeatures.RpcPermissionsV1,
            ],
        };
        var permission = new RuntimeRpcPermissionDescriptor(
            "caller.package",
            "1.0.0",
            new string('a', 64),
            "example.rpc",
            "invoke",
            Requested: true,
            RuntimeRpcPermissionState.Granted,
            Effective: true,
            DateTimeOffset.UtcNow);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(handshake),
            "/api/v1/rpc/catalog" => Json(new RuntimeRpcCatalogSnapshot(2, 3, [], false)),
            "/api/v1/rpc/catalog/events" => Json(new RuntimeRpcCatalogEventPage(2, 3, [], false)),
            "/api/v1/rpc/permissions" => Json(new RuntimeRpcPermissionSnapshot(4, [permission])),
            "/api/v1/rpc/permissions/grant" => Json(new RuntimeRpcPermissionSnapshot(5, [permission])),
            "/api/v1/rpc/permissions/revoke" => Json(new RuntimeRpcPermissionSnapshot(6, [permission with
            {
                State = RuntimeRpcPermissionState.Denied,
                Effective = false,
            }])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var client = new RuntimeManagementClient(() => connection, handler);

        Assert.Equal(2, (await client.GetRpcCatalogAsync()).Revision);
        Assert.Equal(3, (await client.GetRpcCatalogEventsAsync(2, 1)).Sequence);
        Assert.True(Assert.Single((await client.GetRpcPermissionsAsync()).Permissions).Effective);
        Assert.Equal(5, (await client.GrantRpcPermissionAsync("caller.package", "example.rpc", "invoke")).Revision);
        Assert.Equal(6, (await client.RevokeRpcPermissionAsync("caller.package", "example.rpc", "invoke")).Revision);
        Assert.Contains(handler.PathAndQueries, path =>
            path.EndsWith("afterRevision=2&afterSequence=1", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static RuntimeHandshakeResponse CreateHandshake(
        int revision = RuntimeProtocol.CurrentRevision,
        int minimum = RuntimeProtocol.MinimumSupportedRevision,
        int maximum = RuntimeProtocol.MaximumSupportedRevision)
        => new(
            RuntimeProtocol.Identity,
            revision,
            minimum,
            maximum,
            Guid.NewGuid(),
            [RuntimeProtocolFeatures.VersionedApiV1],
            new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "Development", "Development"));

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        Action<HttpRequestMessage>? observe = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> PathAndQueries { get; } = [];
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            PathAndQueries.Add(request.RequestUri.PathAndQuery);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            observe?.Invoke(request);
            return Task.FromResult(send(request));
        }
    }

    private sealed class CancellationBlockingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
