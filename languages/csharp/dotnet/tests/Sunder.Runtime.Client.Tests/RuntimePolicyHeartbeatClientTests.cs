using System.Net;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class RuntimePolicyHeartbeatClientTests
{
    [Fact]
    public void PolicyChannelUsesOnlySynchronousAuthenticatedBoundedExchanges()
    {
        var connection = Connection();
        var handshake = Handshake();
        var session = new RuntimeAppPolicySessionRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid(), handshake.RuntimeInstanceId);
        using var handler = new Handler(request =>
        {
            Assert.Equal("Bearer control-secret", request.Headers.Authorization!.ToString());
            Assert.Equal(HttpVersion.Version11, request.Version);
            if (request.Method == HttpMethod.Get) return Json(handshake);
            using var body = request.Content!.ReadAsStream();
            Assert.Equal(session, JsonSerializer.Deserialize<RuntimeAppPolicySessionRequest>(body, JsonOptions));
            Assert.EndsWith($"/{session.SessionId}/heartbeat", request.RequestUri!.AbsolutePath);
            return Json(new RuntimeAppPolicySessionStatus(session.SessionId, "active", null, false, []));
        });
        using var client = new RuntimePolicyHeartbeatClient(() => connection, handler, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(handshake.RuntimeInstanceId, client.Handshake(timeout.Token).RuntimeInstanceId);
        Assert.Equal(session.SessionId, client.Heartbeat(session, timeout.Token).SessionId);
        Assert.Equal(2, handler.Sends);
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("cross-origin")]
    [InlineData("declared-limit")]
    [InlineData("actual-limit")]
    [InlineData("protocol")]
    [InlineData("credential-replacement")]
    public void PolicyChannelRetainsAuthenticationProtocolAndPayloadBoundaries(string failure)
    {
        var connection = Connection();
        var handshake = Handshake();
        using var handler = new Handler(request =>
        {
            var response = Json(failure == "protocol" ? handshake with { ProtocolIdentity = "unsupported" } : handshake);
            if (failure == "http-error") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            if (failure == "cross-origin") response.RequestMessage = new(HttpMethod.Get, "https://untrusted.invalid/");
            if (failure is "declared-limit" or "actual-limit")
            {
                response.Content = failure == "actual-limit" ? new UnknownLengthContent() : new ByteArrayContent(new byte[129]);
            }
            return response;
        });
        using var client = new RuntimePolicyHeartbeatClient(() => connection, handler,
            new() { MaxHandshakeResponseBytes = failure is "declared-limit" or "actual-limit" ? 128 : 65536 });
        if (failure == "credential-replacement")
        {
            client.Handshake(default);
            connection = connection with { BearerToken = "replacement-secret" };
            Assert.Throws<RuntimeProtocolException>(() => client.Heartbeat(new("session", Guid.NewGuid(), handshake.RuntimeInstanceId), default));
            Assert.Equal(1, handler.Sends);
        }
        else
        {
            var error = Record.Exception(() => client.Handshake(default));
            Assert.NotNull(error);
            Assert.DoesNotContain("control-secret", error.Message);
            if (failure == "http-error") Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<RuntimeClientException>(error).StatusCode);
            else if (failure == "protocol") Assert.IsType<RuntimeProtocolException>(error);
            else Assert.IsType<InvalidDataException>(error);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static RuntimeConnectionInfo Connection() => new(new Uri("http://127.0.0.1:5275/"), "control-secret");
    private static RuntimeHandshakeResponse Handshake() => new(RuntimeProtocol.Identity, 1, 1, 1, Guid.NewGuid(),
        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1], new("Sunder.Runtime.Host", "1.0.0", "1.0.0"));
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Sends;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Sends++;
            var response = respond(request);
            response.RequestMessage ??= request;
            return response;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("The policy channel entered asynchronous I/O.");
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Stream CreateContentReadStream(CancellationToken token) => new MemoryStream(new byte[129]);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("The bounded synchronous reader entered asynchronous I/O.");
    }
}
