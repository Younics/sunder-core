using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Client.Tests;

public sealed class HostManagementClientTests
{
    [Fact]
    public async Task Client_SubmitsAndPollsDurableLifecycleOperation()
    {
        var operationId = "11111111111111111111111111111111";
        var mutation = new HostLifecycleRequest(Guid.NewGuid(), 4);
        var accepted = CreateOperation(operationId, mutation, HostOperationState.Accepted);
        var succeeded = accepted with
        {
            State = HostOperationState.Succeeded,
            UpdatedAtUtc = accepted.UpdatedAtUtc.AddSeconds(1),
            Message = "Runtime worker is ready.",
        };
        var submission = new HostLifecycleSubmission(accepted);
        var requests = new List<(HttpMethod Method, string Uri, string? Authorization)>();
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
                if (request.Method == HttpMethod.Post)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = JsonContent.Create(submission),
                    };
                    response.Headers.Location = new Uri($"/api/host/v1/operations/{operationId}", UriKind.Relative);
                    return Task.FromResult(response);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(succeeded),
                });
            }));

        var response = await client.SubmitRestartRuntimeAsync(mutation);
        var completed = await client.WaitForOperationAsync(response.Operation);

        Assert.Equal(succeeded, completed);
        Assert.Equal(
            [
                (HttpMethod.Post, "https://host.example:5275/api/host/v1/runtime/restart", "Bearer client-token"),
                (HttpMethod.Get, $"https://host.example:5275/api/host/v1/operations/{operationId}", "Bearer client-token"),
            ],
            requests);
    }

    [Fact]
    public async Task Client_RejectsLifecycleSubmissionWithoutMatchingLocation()
    {
        var request = new HostLifecycleRequest(Guid.NewGuid(), 4);
        var operation = CreateOperation(
            "11111111111111111111111111111111",
            request,
            HostOperationState.Accepted) with
        { Kind = HostOperationKinds.RuntimeStart };
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new HostLifecycleSubmission(operation)),
            })));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SubmitStartRuntimeAsync(request));
    }

    [Fact]
    public async Task Client_ShutsDownHostWithBearerAuthentication()
    {
        HttpRequestMessage? recorded = null;
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                recorded = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }));

        await client.ShutdownHostAsync();

        Assert.Equal(HttpMethod.Post, recorded!.Method);
        Assert.Equal("http://127.0.0.1:5275/api/host/v1/shutdown", recorded.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer client-token", recorded.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Client_RequestsBoundedShutdownForUninstall()
    {
        HttpRequestMessage? recorded = null;
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "client-token"),
            new RecordingHandler(request =>
            {
                recorded = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }));

        await client.ShutdownHostForUninstallAsync();

        Assert.Equal(HttpMethod.Post, recorded!.Method);
        Assert.Equal(
            "http://127.0.0.1:5275/api/host/v1/shutdown?mode=uninstall",
            recorded.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer client-token", recorded.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Client_StopsChunkedJsonAtLimitPlusOneAndDisposesResponseStream()
    {
        const int maximumBytes = 1024 * 1024;
        var body = new TrackingReadStream(new byte[maximumBytes + 128]);
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            })));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetStatusAsync());

        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(maximumBytes + 1, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task Client_AcceptsJsonAtExactResponseLimit()
    {
        const int maximumBytes = 1024 * 1024;
        var expected = CreateStatus();
        var json = JsonSerializer.SerializeToUtf8Bytes(expected);
        var payload = Enumerable.Repeat((byte)' ', maximumBytes).ToArray();
        json.CopyTo(payload, 0);
        var body = new TrackingReadStream(payload);
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            })));

        var actual = await client.GetStatusAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(maximumBytes, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task Client_RejectsResponseFromChangedOrigin()
    {
        using var client = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("https://host.example:5275/"), "client-token"),
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/api/host/v1/status"),
                Content = JsonContent.Create(CreateStatus()),
            })));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetStatusAsync());

        Assert.Contains("untrusted origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compatibility_RequiresHostIdentityRangeAndRequestedFeatures()
    {
        var compatible = new HostHandshakeResponse(
            HostProtocol.Identity,
            HostProtocol.CurrentRevision,
            HostProtocol.MinimumSupportedRevision,
            HostProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [HostProtocolFeatures.RuntimeGatewayV1],
            new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "1.0.0"));

        Assert.Null(HostProtocolCompatibility.GetIncompatibility(compatible));
        Assert.Null(HostProtocolCompatibility.GetIncompatibility(
            compatible,
            HostProtocolFeatures.RuntimeGatewayV1));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(
            compatible,
            HostProtocolFeatures.RuntimeLifecycleV1));
        Assert.NotNull(HostProtocolCompatibility.GetIncompatibility(compatible with { HostId = Guid.Empty }));
    }

    private static HostRuntimeStatus CreateStatus()
        => new(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            4,
            Guid.NewGuid(),
            null,
            null,
            null);

    private static HostOperationDescriptor CreateOperation(
        string operationId,
        HostLifecycleRequest request,
        HostOperationState state)
        => new(
            operationId,
            request.MutationId,
            HostOperationKinds.RuntimeRestart,
            request.ExpectedDeploymentGeneration,
            state,
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
            null,
            null);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request);
    }

    private sealed class TrackingReadStream(byte[] content) : Stream
    {
        private int _offset;
        private int _disposed;
        public int BytesRead => Volatile.Read(ref _offset);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }
}
