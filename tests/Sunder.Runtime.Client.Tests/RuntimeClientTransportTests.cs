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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Authorizations.Add(request.Headers.Authorization?.ToString());
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
