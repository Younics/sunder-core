using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
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
        await management.GetRuntimeHandshakeAsync();
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
        Assert.Null(client.ResourceProfile);
    }

    [Fact]
    public async Task ResourceProfile_ReflectsNegotiatedLimitsAndIsDelegatedByCallScope()
    {
        var handler = new InvariantReportHandler(RuntimeResourcePolicies.Large);
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await management.GetRuntimeHandshakeAsync();
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        await client.TryReportInvariantViolationAsync(
            new SunderRpcEndpointReference("rpc1_exact_provider"),
            new InvalidOperationException("failure"));

        Assert.True(client.ResourceLimits.IsRuntimeReported);
        Assert.Equal(
            RuntimeResourcePolicies.Large.MaximumRpcValueBytes,
            client.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
        Assert.Equal(
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Large),
            client.ResourceLimits.Profile?.EffectiveProfileHash);
        var profile = Assert.IsType<SunderRpcResourceProfile>(client.ResourceProfile);
        Assert.Equal(
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Large),
            profile.EffectiveProfileHash);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumRpcValueBytes, profile.MaximumRpcValueBytes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumValueDepth, profile.MaximumValueDepth);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumValueNodes, profile.MaximumValueNodes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumObjectMembers, profile.MaximumObjectMembers);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumInlineDecodedBytes, profile.MaximumInlineDecodedBytes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumContentReferenceBytes, profile.MaximumContentReferenceBytes);

        await using var scope = await client.CreateCallScopeAsync();
        Assert.Same(profile, scope.ResourceProfile);
    }

    [Fact]
    public async Task Invoke_PreservesRuntimeDomainErrorDetails()
    {
        var handler = new InvariantReportHandler();
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            "rpc1_exact_provider",
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            null,
            CancellationToken.None).AsTask());

        Assert.Equal(SunderRpcErrorKind.Domain, failure.Error.Kind);
        Assert.Equal("duplicate", failure.Error.Details!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CallScopeDisposal_RuntimeUnavailable_DoesNotLogExpectedRestartFailure()
    {
        var handler = new InvariantReportHandler
        {
            CallScopeCloseStatusCode = HttpStatusCode.ServiceUnavailable,
        };
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        var failureLogged = false;
        void Observe(AppSessionLogSnapshotEntry entry)
            => failureLogged |= string.Equals(
                entry.Message,
                "Failed to close a package App RPC call scope.",
                StringComparison.Ordinal);

        AppSessionLog.EntryWritten += Observe;
        try
        {
            await using var client = new RuntimeAppWebRpcClient(management, "app-session");
            var scope = await client.CreateCallScopeAsync();

            await scope.DisposeAsync();

            Assert.Equal(1, handler.CallScopeCloseCount);
            Assert.False(failureLogged);
        }
        finally
        {
            AppSessionLog.EntryWritten -= Observe;
        }
    }

    [Fact]
    public async Task CreateCallScope_PreservesRetiredAppSessionRpcErrorWithoutRetry()
    {
        var handler = new InvariantReportHandler
        {
            CallScopeOpenError = new RuntimeRpcErrorDescriptor(
                RuntimeRpcErrorKind.Unavailable,
                "rpc.app-session.retired",
                "The App RPC caller session is unavailable or retired.",
                RuntimeRpcDispatchState.NotDispatched,
                RuntimeRpcRetrySafety.Safe),
        };
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.CreateCallScopeAsync().AsTask());

        Assert.Equal(SunderRpcErrorKind.Unavailable, exception.Error.Kind);
        Assert.Equal("rpc.app-session.retired", exception.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, exception.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, exception.Error.RetrySafety);
        Assert.Equal(1, handler.CallScopeOpenCount);
    }

    private sealed class InvariantReportHandler(RuntimeResourcePolicy? resourcePolicy = null) : HttpMessageHandler
    {
        public HttpStatusCode CallScopeCloseStatusCode { get; init; } = HttpStatusCode.NoContent;

        public RuntimeRpcErrorDescriptor? CallScopeOpenError { get; init; }

        public int CallScopeOpenCount { get; private set; }

        public int CallScopeCloseCount { get; private set; }

        public string? InvariantReportPath { get; private set; }

        public RuntimeRpcAppInvariantViolationRequest? InvariantReport { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                var features = new List<string>
                {
                    RuntimeProtocolFeatures.VersionedApiV1,
                    RuntimeProtocolFeatures.AppWebRpcV2,
                };
                RuntimeResourceProfileDescriptor? profile = null;
                if (resourcePolicy is not null)
                {
                    features.Add(RuntimeProtocolFeatures.RuntimeResourcePolicyV1);
                    profile = new RuntimeResourceProfileDescriptor(
                        RuntimeResourcePolicyContract.GetEffectiveProfileHash(resourcePolicy),
                        resourcePolicy.MaximumRpcValueBytes,
                        resourcePolicy.MaximumValueDepth,
                        resourcePolicy.MaximumValueNodes,
                        resourcePolicy.MaximumObjectMembers,
                        resourcePolicy.MaximumInlineDecodedBytes,
                        resourcePolicy.MaximumContentReferenceBytes);
                }
                return Json(new RuntimeHandshakeResponse(
                    RuntimeProtocol.Identity,
                    RuntimeProtocol.CurrentRevision,
                    RuntimeProtocol.MinimumSupportedRevision,
                    RuntimeProtocol.MaximumSupportedRevision,
                    Guid.NewGuid(),
                    features,
                    new RuntimeProductVersionDiagnostics(
                        "Sunder.Runtime.Host",
                        "Development",
                        "Development"),
                    profile));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/invariant-violation")
            {
                InvariantReportPath = request.RequestUri.AbsolutePath;
                InvariantReport = await request.Content!
                    .ReadFromJsonAsync<RuntimeRpcAppInvariantViolationRequest>(cancellationToken);
                return Json(new RuntimeRpcAppInvariantViolationResponse(true, null));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/call-scopes/open")
            {
                CallScopeOpenCount++;
                if (CallScopeOpenError is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(CallScopeOpenError),
                    };
                }
                return Json(new RuntimeRpcAppCallScopeDescriptor(
                    "call-scope",
                    DateTimeOffset.UtcNow.AddMinutes(1)));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/call-scopes/close")
            {
                CallScopeCloseCount++;
                return new HttpResponseMessage(CallScopeCloseStatusCode);
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/invoke")
            {
                return Json(new RuntimeRpcAppInvokeResponse(
                    null,
                    new RuntimeRpcErrorDescriptor(
                        RuntimeRpcErrorKind.Domain,
                        "messages.rejected",
                        "Rejected.",
                        RuntimeRpcDispatchState.PossiblyDispatched,
                        RuntimeRpcRetrySafety.Unsafe,
                        JsonSerializer.SerializeToElement(new { reason = "duplicate" }))));
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
