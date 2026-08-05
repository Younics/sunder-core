using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeAppWebRpcClientTests
{
    [Fact]
    public async Task InvariantReport_UsesExactAppRouteAndSanitizedMessage()
    {
        var handler = new InvariantReportHandler();
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        var management = new RuntimeManagementClient(() => connection, handler);
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");
        var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");

        var accepted = await client.TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("line one\r\nline two " + new string('x', 600)));

        Assert.True(accepted);
        Assert.Equal("/api/v1/rpc/app/invariant-violation", handler.InvariantReportPath);
        var request = Assert.IsType<RuntimeRpcAppInvariantViolationRequest>(handler.InvariantReport);
        Assert.Equal("app-session", request.SessionId);
        Assert.Equal(endpoint.Value, request.EndpointReference);
        Assert.Null(request.CallScopeId);
        Assert.Equal(512, request.ExceptionMessage.Length);
        Assert.DoesNotContain('\r', request.ExceptionMessage);
        Assert.DoesNotContain('\n', request.ExceptionMessage);
        Assert.DoesNotContain(nameof(InvalidOperationException), request.ExceptionMessage, StringComparison.Ordinal);
    }

    private sealed class InvariantReportHandler : HttpMessageHandler
    {
        public string? InvariantReportPath { get; private set; }

        public RuntimeRpcAppInvariantViolationRequest? InvariantReport { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                return Json(new RuntimeHandshakeResponse(
                    RuntimeProtocol.Identity,
                    RuntimeProtocol.CurrentRevision,
                    RuntimeProtocol.MinimumSupportedRevision,
                    RuntimeProtocol.MaximumSupportedRevision,
                    Guid.NewGuid(),
                    [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AppWebRpcV1],
                    new RuntimeProductVersionDiagnostics(
                        "Sunder.Runtime.Host",
                        "Development",
                        "Development")));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/invariant-violation")
            {
                InvariantReportPath = request.RequestUri.AbsolutePath;
                InvariantReport = await request.Content!
                    .ReadFromJsonAsync<RuntimeRpcAppInvariantViolationRequest>(cancellationToken);
                return Json(new RuntimeRpcAppInvariantViolationResponse(true, null));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app-sessions/close")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
