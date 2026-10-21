using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeAppWorkerExecutionObservationTests
{
    [Fact]
    public async Task CaptureTransportsExactHostProviderDescriptorAndCallerSession()
    {
        var handler = new Handler();
        await using var client = Client(handler);
        Assert.Equal(Reference, await client.CaptureWorkerExecutionAsync(Provider));
        var request = handler.Capture!;
        Assert.Equal("current-app-session", request.SessionId);
        Assert.Null(request.CallScopeId);
        Assert.Equal(Provider.ActivationId, request.Provider.ActivationId);
        Assert.Equal(Provider.ActivationEpoch, request.Provider.ActivationEpoch);
        Assert.Equal(Provider.SessionGeneration, request.Provider.SessionGeneration);
        Assert.Equal(Provider.Endpoint.Value, request.Provider.EndpointReference);
        Assert.Equal(Provider.DescriptorSha256, request.Provider.DescriptorSha256);
        Assert.Equal(Provider.AbiSha256, request.Provider.AbiSha256);
        Assert.Equal(Provider.PackageId, request.Provider.PackageId);
        Assert.Equal(Provider.PackageVersion, request.Provider.PackageVersion);
        Assert.Equal(Provider.ProviderId, request.Provider.ProviderId);
        Assert.Equal(Provider.ContractId, request.Provider.ContractId);
        Assert.Equal(Provider.ContractVersion, request.Provider.ContractVersion);
    }

    [Fact]
    public async Task CurrentSessionMayQueryHistoricalReferenceWithoutReplacingItsOrigin()
    {
        var handler = new Handler();
        await using var client = Client(handler);
        var result = await client.ObserveWorkerExecutionAsync(Reference);
        Assert.NotEqual(handler.RuntimeId, Reference.RuntimeInstanceId);
        Assert.Equal(Reference, result.Reference);
        Assert.Equal(PackageWorkerExecutionState.CleanupConfirmed, result.State);
        Assert.Equal("current-app-session", handler.Observe!.SessionId);
        Assert.Equal(Reference.RuntimeInstanceId, handler.Observe.Reference.RuntimeInstanceId);
        Assert.Null(handler.Observe.CallScopeId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown-state")]
    [InlineData("different-reference")]
    [InlineData("unconfirmed-time")]
    [InlineData("invalid-reference")]
    public async Task MalformedEvidenceFailsClosedAtAppTransportBoundary(string mutation)
    {
        var handler = new Handler { Mutation = mutation };
        await using var client = Client(handler);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.ObserveWorkerExecutionAsync(Reference).AsTask());
        Assert.Equal(SunderRpcErrorKind.Protocol, error.Error.Kind);
    }

    [Fact]
    public async Task StructuredPermissionFailurePreservesDispatchAndRetryMetadata()
    {
        var handler = new Handler { Error = new(RuntimeRpcErrorKind.PermissionDenied, "rpc.observation-denied", "Denied.",
            RuntimeRpcDispatchState.NotDispatched, RuntimeRpcRetrySafety.Safe) };
        await using var client = Client(handler);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.ObserveWorkerExecutionAsync(Reference).AsTask());
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, error.Error.Kind);
        Assert.Equal("rpc.observation-denied", error.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
    }

    private static readonly PackageWorkerExecutionReference Reference = new(Guid.NewGuid(), "historical-worker-reference");
    private static readonly SunderRpcProviderSnapshot Provider = new("test.package", "1.0.0", "test.provider", "test.contract", "1.0.0",
        new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, new("exact-provider-endpoint"), 4, SunderRpcProviderState.Active);
    private static RuntimeAppWebRpcClient Client(Handler handler) => new(new RuntimeManagementClient(
        () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "test-token"), handler), "current-app-session");

    private sealed class Handler : HttpMessageHandler
    {
        internal Guid RuntimeId { get; } = Guid.NewGuid();
        internal string? Mutation;
        internal RuntimeRpcErrorDescriptor? Error;
        internal RuntimeRpcAppExecutionCaptureRequest? Capture;
        internal RuntimeRpcAppExecutionObserveRequest? Observe;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
                return Json(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, 1, 1, 1, RuntimeId,
                    [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1,
                        RuntimeProtocolFeatures.AppWebRpcV1, RuntimeProtocolFeatures.WorkerExecutionObservationsV1],
                    new("Sunder.Runtime.Host", "Development", "Development")));
            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app-sessions/close")
                return new(HttpStatusCode.NoContent);

            // Runtime.Client's owning tests select the endpoint spelling. These App
            // tests own DTO conversion, not a second independent route declaration.
            Assert.StartsWith("/api/v1/rpc/app/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var value = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            if (value.TryGetProperty("provider", out _))
            {
                Capture = value.Deserialize<RuntimeRpcAppExecutionCaptureRequest>(options)!;
                return Json(new { reference = new { Reference.RuntimeInstanceId, Reference.Value }, error = Error });
            }
            Observe = value.Deserialize<RuntimeRpcAppExecutionObserveRequest>(options)!;
            if (Mutation == "missing") return Json(new { observation = (object?)null, error = Error });
            return Json(new
            {
                observation = new
                {
                    reference = new
                    {
                        runtimeInstanceId = Mutation == "invalid-reference" ? Guid.Empty : Reference.RuntimeInstanceId,
                        value = Mutation == "different-reference" ? "replacement-reference" : Reference.Value,
                    },
                    state = Mutation == "unknown-state" ? 99 : Mutation == "unconfirmed-time" ? 1 : 2,
                    confirmedAtUtc = DateTimeOffset.UtcNow,
                },
                error = Error,
            });
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
