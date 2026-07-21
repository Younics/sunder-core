using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
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
