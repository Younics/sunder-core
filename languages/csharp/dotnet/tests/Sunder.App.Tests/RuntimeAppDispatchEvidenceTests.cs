using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeAppDispatchEvidenceTests
{
    [Fact]
    public async Task ExplicitAuthenticatedInvokeRejectionQualifiesOnlyInsideItsSdkOperation()
    {
        var handler = new Handler("invoke");
        await using var client = Client(handler);
        var raw = await Assert.ThrowsAsync<SunderRpcException>(() => ((ISunderRpcClient)client).InvokeAsync(Provider.Endpoint,
            "builder", "start", JsonSerializer.SerializeToElement(new { })).AsTask());
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(raw));
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
        Assert.Equal("rpc.test.rejected", error.Error.Code);
        Assert.Equal(2, handler.InvokeRequests);
    }

    [Fact]
    public async Task AuthenticatedMetadataFailureAfterSuccessfulInvokeNeverProvesNoStart()
    {
        var handler = new Handler("metadata");
        await using var client = Client(handler);
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        Assert.Equal(1, handler.InvokeRequests);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
    }

    [Fact]
    public async Task AuxiliaryMetadataBeforeInvokeCannotAuthenticateAnOperationRejection()
    {
        var handler = new Handler("metadata-before");
        await using var client = Client(handler);
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(0, handler.InvokeRequests);
    }

    [Fact]
    public async Task AuthenticatedCancellationProofSurvivesOnlySdkTranslationWithTheOriginalToken()
    {
        var handler = new Handler("cancelled");
        await using var client = Client(handler);
        await using var resources = new SunderRpcResourceLifetime();
        var remote = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var translated = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(remote, caller.Token));
        Assert.Equal(caller.Token, translated.CancellationToken);
        Assert.Same(remote, translated.InnerException);
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(translated));
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(new OperationCanceledException("Untrusted wrapper", remote, caller.Token)));
        Assert.Equal(1, handler.InvokeRequests);
    }

    [Fact]
    public async Task ActualInvokeHostRetryMetadataIsPreservedWithoutManufacturingNoDispatchProof()
    {
        var handler = new Handler("idempotent-possible");
        await using var client = Client(handler);
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(1, handler.InvokeRequests);
    }

    [Theory]
    [InlineData("invalid-dispatch")]
    [InlineData("invalid-retry")]
    [InlineData("invalid-kind")]
    [InlineData("missing-kind")]
    [InlineData("missing-code")]
    [InlineData("blank-code")]
    [InlineData("domain")]
    [InlineData("possibly-dispatched")]
    [InlineData("result-and-error")]
    [InlineData("non-object-root")]
    [InlineData("non-object-error")]
    [InlineData("null-root")]
    public async Task MissingOrInvalidWireEvidenceAndDomainsNeverAuthenticateNoDispatch(string mutation)
    {
        var handler = new Handler(mutation);
        await using var client = Client(handler);
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Record.ExceptionAsync(() => StartLike(client, resources));
        Assert.NotNull(error);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(1, handler.InvokeRequests);
        if (mutation.StartsWith("invalid-", StringComparison.Ordinal))
        {
            // Undefined enums remain neutral DTO values. The App, not a global
            // DTO converter, rejects them at its error-translation boundary.
            var protocol = Assert.IsType<SunderRpcException>(error);
            Assert.Equal(SunderRpcErrorKind.Protocol, protocol.Error.Kind);
            Assert.Equal("rpc.transport.invalid-error", protocol.Error.Code);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NeutralErrorWireRetainsDefaultOmissionEqualityAndConstructorSignatures(bool ignoreDefaults, bool unsafeFlags)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = ignoreDefaults ? System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault : System.Text.Json.Serialization.JsonIgnoreCondition.Never };
        var error = new RuntimeRpcErrorDescriptor(RuntimeRpcErrorKind.Unavailable, "rpc.test", "Unavailable.",
            unsafeFlags ? RuntimeRpcDispatchState.PossiblyDispatched : RuntimeRpcDispatchState.NotDispatched,
            unsafeFlags ? RuntimeRpcRetrySafety.Unsafe : RuntimeRpcRetrySafety.Safe);
        var json = JsonSerializer.SerializeToElement(error, options);
        Assert.Equal(!ignoreDefaults || unsafeFlags, json.TryGetProperty("dispatchState", out _));
        Assert.Equal(!ignoreDefaults || unsafeFlags, json.TryGetProperty("retrySafety", out _));
        Assert.Equal(error, json.Deserialize<RuntimeRpcErrorDescriptor>(options));
        Assert.Null(typeof(RuntimeRpcErrorDescriptor).GetProperty(nameof(RuntimeRpcErrorDescriptor.DispatchState))!.SetMethod);
        Assert.Null(typeof(RuntimeRpcErrorDescriptor).GetProperty(nameof(RuntimeRpcErrorDescriptor.RetrySafety))!.SetMethod);
        var constructor = Assert.Single(typeof(RuntimeRpcErrorDescriptor).GetConstructors());
        Assert.Equal(6, constructor.GetParameters().Length);
        Assert.True(constructor.GetParameters()[3].IsOptional);
        Assert.True(constructor.GetParameters()[4].IsOptional);
        // Independent pre-draft shape, not an old binary compatibility claim.
        var oldReader = json.Deserialize<PreDraftErrorShape>(options)!;
        Assert.Equal(error.Kind, oldReader.Kind);
        Assert.Equal(error.DispatchState, oldReader.DispatchState);
        Assert.Equal(error.RetrySafety, oldReader.RetrySafety);
    }

    [Fact]
    public void PreDraftDefaultAndOmittedFlagWireKeepLegacyConstructorDefaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = new PreDraftErrorShape(RuntimeRpcErrorKind.Unavailable, "rpc.test", "Unavailable.");
        var json = JsonSerializer.Serialize(legacy, options);
        var current = JsonSerializer.Deserialize<RuntimeRpcErrorDescriptor>(json, options)!;
        Assert.Equal(legacy.Kind, current.Kind);
        Assert.Equal(legacy.DispatchState, current.DispatchState);
        Assert.Equal(legacy.RetrySafety, current.RetrySafety);
        options = new(options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };
        var incomplete = JsonSerializer.Serialize(legacy, options);
        var fromOmitted = JsonSerializer.Deserialize<RuntimeRpcErrorDescriptor>(incomplete, options);
        Assert.Equal(new RuntimeRpcErrorDescriptor(legacy.Kind, legacy.Code, legacy.Message), fromOmitted);
    }

    [Theory]
    [InlineData("missing-dispatch")]
    [InlineData("missing-retry")]
    [InlineData("missing-both")]
    [InlineData("duplicate-dispatch")]
    [InlineData("duplicate-dispatch-case")]
    [InlineData("duplicate-retry")]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-code")]
    [InlineData("duplicate-error")]
    [InlineData("duplicate-error-case")]
    [InlineData("duplicate-value")]
    public async Task PublicRuntimeClientKeepsLegacyOmissionAndLastWinsButAppCannotTrustThem(string mode)
    {
        var publicHandler = new Handler(mode);
        using (var management = Management(publicHandler))
        {
            var response = await management.InvokeAppRpcAsync(InvokeRequest());
            Assert.Null(response.Value);
            Assert.Equal(new RuntimeRpcErrorDescriptor(RuntimeRpcErrorKind.Unavailable, "rpc.test.rejected", "Rejected."), response.Error);
        }
        Assert.Equal(1, publicHandler.InvokeRequests);

        var appHandler = new Handler(mode);
        await using var client = Client(appHandler);
        await using var resources = new SunderRpcResourceLifetime();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => StartLike(client, resources));
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(1, appHandler.InvokeRequests);
    }

    [Fact]
    public async Task ActualInvokeSuccessHasNoErrorMetadataEvidence()
    {
        var handler = new Handler("success");
        using var management = Management(handler);
        var (response, hasExplicitValidDispatchMetadata) = await management.InvokeAppRpcWithDispatchMetadataAsync(InvokeRequest());
        Assert.Null(response.Error);
        Assert.Equal(1, response.Value!.Value.GetInt32());
        Assert.False(hasExplicitValidDispatchMetadata);
        Assert.Equal(1, handler.InvokeRequests);
    }

    private sealed record PreDraftErrorShape(RuntimeRpcErrorKind Kind, string Code, string Message,
        RuntimeRpcDispatchState DispatchState = RuntimeRpcDispatchState.NotDispatched,
        RuntimeRpcRetrySafety RetrySafety = RuntimeRpcRetrySafety.Safe);

    // Owning App transport probe for the unchanged emitter's operation order. The
    // separate Build test actually emits/compiles a Start-like generated client.
    private static async Task<int> StartLike(ISunderRpcClient client, SunderRpcResourceLifetime resources)
    {
        await using var call = await resources.CreateCallAsync(client, Provider.Endpoint);
        await SunderRpcServiceCall.EnsureCurrentAsync((ISunderRpcCallScope)call.Client!, Provider);
        var response = await call.Client!.InvokeAsync(Provider.Endpoint, "builder", "start", JsonSerializer.SerializeToElement(new { }));
        await call.Client.GetProviderAsync(Provider.Endpoint); // Actual authenticated metadata, not the Invoke response.
        return await call.DeserializeAsync<int>(response);
    }

    private static RuntimeAppWebRpcClient Client(Handler handler) => new(Management(handler), "current-app-session");
    private static RuntimeManagementClient Management(Handler handler) => new(
        () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "test-token"), handler);
    private static RuntimeRpcAppInvokeRequest InvokeRequest() => new("current-app-session", Provider.Endpoint.Value,
        "builder", "start", JsonSerializer.SerializeToElement(new { }), DeadlineUtc: null);
    private static readonly SunderRpcProviderSnapshot Provider = new("example.builder", "1.0.0", "example.builder.provider", "example.builder", "1.0.0",
        new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, new("rpc1_exact"), 4, SunderRpcProviderState.Active);

    private sealed class Handler(string mode) : HttpMessageHandler
    {
        internal int InvokeRequests;
        private readonly Guid runtime = Guid.NewGuid();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/handshake") return Json(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, 1, 1, 1, runtime,
                [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1, RuntimeProtocolFeatures.AppWebRpcV1],
                new("Sunder.Runtime.Host", "Development", "Development")));
            if (path.EndsWith("call-scopes/open", StringComparison.Ordinal)) return Json(new RuntimeRpcAppCallScopeDescriptor("scope", DateTimeOffset.UtcNow.AddMinutes(1)));
            if (path.EndsWith("call-scopes/close", StringComparison.Ordinal) || path.EndsWith("app-sessions/close", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (path.EndsWith("/provider", StringComparison.Ordinal))
            {
                if (mode == "metadata-before" || mode == "metadata" && InvokeRequests > 0) return Json(new RuntimeRpcAppProviderResponse(null, Rejected()));
                return Json(new RuntimeRpcAppProviderResponse(new(Provider.PackageId, Provider.PackageVersion, Provider.ProviderId,
                    Provider.ContractId, Provider.ContractVersion, Provider.DescriptorSha256, Provider.AbiSha256, Provider.ActivationId,
                    Provider.ActivationEpoch, Provider.SessionGeneration, Provider.Endpoint.Value, Provider.CatalogRevision, RuntimeRpcProviderState.Active, null), null));
            }
            if (path.EndsWith("/invoke", StringComparison.Ordinal))
            {
                InvokeRequests++;
                if (mode is "metadata" or "success") return Json(new RuntimeRpcAppInvokeResponse(JsonSerializer.SerializeToElement(1), null));
                var body = JsonSerializer.SerializeToNode(new RuntimeRpcAppInvokeResponse(null, Rejected()), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var error = body["error"]!.AsObject();
                if (mode == "missing-dispatch") error.Remove("dispatchState");
                if (mode == "missing-retry") error.Remove("retrySafety");
                if (mode == "missing-both") { error.Remove("dispatchState"); error.Remove("retrySafety"); }
                if (mode == "missing-kind") error.Remove("kind");
                if (mode == "missing-code") error.Remove("code");
                if (mode == "blank-code") error["code"] = " ";
                if (mode == "invalid-dispatch") error["dispatchState"] = 999;
                if (mode == "invalid-retry") error["retrySafety"] = 999;
                if (mode == "invalid-kind") error["kind"] = 999;
                if (mode == "domain") error["kind"] = 0;
                if (mode == "cancelled") error["kind"] = (int)RuntimeRpcErrorKind.Cancelled;
                if (mode == "possibly-dispatched")
                {
                    error["dispatchState"] = (int)RuntimeRpcDispatchState.PossiblyDispatched;
                    error["retrySafety"] = (int)RuntimeRpcRetrySafety.Unsafe;
                }
                if (mode == "result-and-error") body["value"] = 1;
                if (mode == "idempotent-possible") error["dispatchState"] = (int)RuntimeRpcDispatchState.PossiblyDispatched;
                var duplicateMember = mode switch
                {
                    "duplicate-dispatch" => "\"dispatchState\":1,",
                    "duplicate-dispatch-case" => "\"DispatchState\":1,",
                    "duplicate-retry" => "\"retrySafety\":1,",
                    "duplicate-kind" => "\"kind\":0,",
                    "duplicate-code" => "\"code\":\"rpc.other\",",
                    _ => null,
                };
                // JsonObject intentionally cannot preserve duplicate properties.
                // Write raw duplicates with a valid last value so a new strict
                // global serializer cannot accidentally satisfy these controls.
                var json = duplicateMember is null ? body.ToJsonString()
                    : "{\"value\":null,\"error\":{" + duplicateMember + error.ToJsonString()[1..] + "}";
                if (mode == "duplicate-error") json = "{\"error\":null," + json[1..];
                if (mode == "duplicate-error-case") json = "{\"Error\":null," + json[1..];
                if (mode == "duplicate-value") json = "{\"value\":null," + json[1..];
                if (mode == "non-object-root") json = "[]";
                if (mode == "non-object-error") json = "{\"error\":[]}";
                if (mode == "null-root") json = "null";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static RuntimeRpcErrorDescriptor Rejected() => new(RuntimeRpcErrorKind.Unavailable, "rpc.test.rejected", "Rejected.",
            RuntimeRpcDispatchState.NotDispatched, RuntimeRpcRetrySafety.Safe);
        private static Task<HttpResponseMessage> Json<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
    }
}
