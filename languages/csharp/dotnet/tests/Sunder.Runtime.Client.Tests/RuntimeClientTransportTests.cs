using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class RuntimeClientTransportTests
{
    [Fact]
    public async Task AppCallScopeOpen_ReturnsTypedRpcErrorResult()
    {
        var expected = new RuntimeRpcErrorDescriptor(
            RuntimeRpcErrorKind.Unavailable,
            "rpc.app-session.retired",
            "The App RPC caller session is unavailable or retired.",
            RuntimeRpcDispatchState.NotDispatched,
            RuntimeRpcRetrySafety.Safe);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateResourceHandshake(RuntimeResourcePolicies.Standard)),
            "/api/v1/rpc/app/call-scopes/open" => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(expected),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var result = await client.OpenAppRpcCallScopeAsync(
            new RuntimeRpcAppCallScopeOpenRequest("retired-session", null));

        Assert.Null(result.Scope);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task AppCallScopeOpen_DoesNotDecodeProblemDetailsAsRpcError()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateResourceHandshake(RuntimeResourcePolicies.Standard)),
            "/api/v1/rpc/app/call-scopes/open" => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "{\"title\":\"Internal Runtime error\",\"code\":\"runtime.v1.internal\"}",
                    Encoding.UTF8,
                    "application/problem+json"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var exception = await Assert.ThrowsAsync<RuntimeClientException>(() =>
            client.OpenAppRpcCallScopeAsync(
                new RuntimeRpcAppCallScopeOpenRequest("app-session", null)));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("runtime.v1.internal", exception.ErrorCode);
    }

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
    public async Task RegistrySourceAdoption_UsesDedicatedRuntimeEndpointAndPreservesSafetyMode()
    {
        RuntimeRegistrySourceAdoptionRequest? captured = null;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/registry/packages/adopt-source" => Json(new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Previewed.",
                false,
                false,
                [],
                [],
                [],
                [])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/registry/packages/adopt-source")
            {
                captured = request.Content!.ReadFromJsonAsync<RuntimeRegistrySourceAdoptionRequest>()
                    .GetAwaiter()
                    .GetResult();
            }
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await client.AdoptRegistryPackageSourceAsync(new RuntimeRegistrySourceAdoptionRequest(
            "demo",
            "https://registry.test/",
            Tag: "latest",
            DryRun: true));

        Assert.Equal(
            ["/api/handshake", "/api/v1/registry/packages/adopt-source"],
            handler.Paths);
        Assert.Equal("demo", captured?.PackageId);
        Assert.True(captured?.DryRun);
        Assert.False(captured?.Confirm);
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
    public async Task ResourceProfile_RefreshesWithReplacementRuntimeInstance()
    {
        var first = CreateResourceHandshake(RuntimeResourcePolicies.Standard);
        var second = CreateResourceHandshake(RuntimeResourcePolicies.Large);
        var current = first;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(current),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        using var transport = new RuntimeClientTransport(() => connection, handler);

        await transport.NegotiateAsync();

        Assert.Equal(first.RuntimeInstanceId, transport.CurrentResourceProfile?.RuntimeInstanceId);
        Assert.Equal(8 * RuntimeResourcePolicies.Mebibyte, transport.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
        Assert.Equal(first.ResourceProfile!.EffectiveProfileHash, transport.CurrentResourceProfile?.EffectiveProfileHash);

        current = second;
        await transport.RefreshHandshakeAsync();

        Assert.Equal(second.RuntimeInstanceId, transport.CurrentResourceProfile?.RuntimeInstanceId);
        Assert.Equal(16 * RuntimeResourcePolicies.Mebibyte, transport.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
        Assert.Equal(second.ResourceProfile!.EffectiveProfileHash, transport.CurrentResourceProfile?.EffectiveProfileHash);
    }

    [Fact]
    public async Task ResourceProfile_RequiresClosedDescriptorAndMatchingHash()
    {
        var valid = CreateResourceHandshake(RuntimeResourcePolicies.Standard);
        var currentJson = JsonSerializer.Serialize(valid with
        {
            ResourceProfile = valid.ResourceProfile! with { EffectiveProfileHash = new string('0', 64) },
        });
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(currentJson, Encoding.UTF8, "application/json"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var transport = new RuntimeClientTransport(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await Assert.ThrowsAsync<RuntimeProtocolException>(() => transport.NegotiateAsync());
        Assert.Null(transport.CurrentResourceProfile);

        currentJson = JsonSerializer.Serialize(valid).Replace(
            $"\"maximumContentReferenceBytes\":{RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes}",
            $"\"maximumContentReferenceBytes\":{RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes},\"extra\":true",
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => transport.NegotiateAsync());
        Assert.Null(transport.CurrentResourceProfile);
    }

    [Fact]
    public async Task RpcRequestPreflight_UsesExactFinalUtf8ValueBytesEscapingAndDepth()
    {
        var policy = CreateResourcePolicy(RuntimeResourcePolicies.Mebibyte, maximumValueDepth: 16);
        var handshake = CreateResourceHandshake(policy);
        var capturedValues = new List<JsonElement>();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(handshake),
            "/api/v1/rpc/app/invoke" => Json(new RuntimeRpcAppInvokeResponse(
                JsonSerializer.SerializeToElement(true),
                null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/rpc/app/invoke") return;
            var body = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            capturedValues.Add(document.RootElement.GetProperty("request").Clone());
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await client.InvokeAppRpcAsync(Invocation(StringWithSerializedSize(
            policy.MaximumRpcValueBytes,
            escapedQuotes: false)));
        await client.InvokeAppRpcAsync(Invocation(StringWithSerializedSize(
            policy.MaximumRpcValueBytes,
            escapedQuotes: true)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAppRpcAsync(Invocation(
            StringWithSerializedSize(policy.MaximumRpcValueBytes + 1, escapedQuotes: false))));
        await client.InvokeAppRpcAsync(Invocation(NestedArray(depth: policy.MaximumValueDepth)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAppRpcAsync(
            Invocation(NestedArray(depth: policy.MaximumValueDepth + 1))));

        Assert.Equal(3, capturedValues.Count);
        Assert.Equal(
            policy.MaximumRpcValueBytes,
            Encoding.UTF8.GetByteCount(capturedValues[0].GetRawText()));
        Assert.Equal(
            policy.MaximumRpcValueBytes,
            Encoding.UTF8.GetByteCount(capturedValues[1].GetRawText()));
    }

    [Fact]
    public async Task RpcRequestAndResult_AllowNegotiatedDepth64AndRejectValueDepth65()
    {
        var responseDepth = 64;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateResourceHandshake(RuntimeResourcePolicies.Standard)),
            "/api/v1/rpc/app/invoke" => RawInvokeResponse(NestedArray(responseDepth)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var exact = await client.InvokeAppRpcAsync(Invocation(NestedArray(depth: 64)));

        Assert.NotNull(exact.Value);
        responseDepth = 65;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAppRpcAsync(
            Invocation(JsonSerializer.SerializeToElement(true))));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 12)]
    public async Task RpcResponse_AllowsReportedStandardAndLargeValuesBeyondLegacyParserCap(
        bool large,
        int payloadMebibytes)
    {
        var policy = large ? RuntimeResourcePolicies.Large : RuntimeResourcePolicies.Standard;
        var value = StringWithSerializedSize(
            payloadMebibytes * RuntimeResourcePolicies.Mebibyte,
            escapedQuotes: false);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateResourceHandshake(policy)),
            "/api/v1/rpc/app/invoke" => Json(new RuntimeRpcAppInvokeResponse(value, null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var response = await client.InvokeAppRpcAsync(Invocation(JsonSerializer.SerializeToElement(true)));

        Assert.Equal(
            payloadMebibytes * RuntimeResourcePolicies.Mebibyte,
            Encoding.UTF8.GetByteCount(response.Value!.Value.GetRawText()));
    }

    [Fact]
    public async Task RpcResponseAndNdjson_EnforceValueAndEnvelopeParserCaps()
    {
        var policy = CreateResourcePolicy(RuntimeResourcePolicies.Mebibyte, maximumValueDepth: 16);
        var handshake = CreateResourceHandshake(policy);
        var responseValue = StringWithSerializedSize(policy.MaximumRpcValueBytes + 1, escapedQuotes: false);
        var oversizedBody = new byte[policy.MaximumRpcValueBytes
                                     + RuntimeClientResourceLimits.EnvelopeByteOverhead
                                     + 1];
        Array.Fill(oversizedBody, (byte)'x');
        var oversizedLine = new byte[policy.MaximumRpcValueBytes
                                     + RuntimeClientResourceLimits.EnvelopeByteOverhead
                                     + 2];
        Array.Fill(oversizedLine, (byte)'x');
        oversizedLine[^1] = (byte)'\n';
        var returnOversizedBody = false;
        var returnOversizedLine = false;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(handshake),
            "/api/v1/rpc/app/invoke" when returnOversizedBody => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversizedBody),
            },
            "/api/v1/rpc/app/invoke" => Json(new RuntimeRpcAppInvokeResponse(responseValue, null)),
            "/api/v1/rpc/app/subscribe" when returnOversizedLine => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(oversizedLine, writable: false)),
            },
            "/api/v1/rpc/app/subscribe" => Ndjson(
                new RuntimeRpcAppSubscriptionFrame(
                    RuntimeRpcAppStreamFrameTypes.Event,
                    StringWithSerializedSize(policy.MaximumRpcValueBytes, escapedQuotes: false),
                    null),
                new RuntimeRpcAppSubscriptionFrame(RuntimeRpcAppStreamFrameTypes.Completed, null, null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAppRpcAsync(
            Invocation(JsonSerializer.SerializeToElement(true))));
        returnOversizedBody = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAppRpcAsync(
            Invocation(JsonSerializer.SerializeToElement(true))));
        returnOversizedBody = false;

        var frames = new List<RuntimeRpcAppSubscriptionFrame>();
        await foreach (var frame in client.SubscribeAppRpcAsync(
                           Invocation(JsonSerializer.SerializeToElement(true))))
        {
            frames.Add(frame);
        }
        Assert.Equal(2, frames.Count);
        Assert.Equal(
            policy.MaximumRpcValueBytes,
            Encoding.UTF8.GetByteCount(frames[0].Event!.Value.GetRawText()));

        returnOversizedLine = true;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in client.SubscribeAppRpcAsync(
                               Invocation(JsonSerializer.SerializeToElement(true))))
            {
            }
        });
    }

    [Fact]
    public async Task Handshake_RemainsUnderFixedParserCapBeforeResourceProfileExists()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[64 * 1024 + 1]),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var transport = new RuntimeClientTransport(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.NegotiateAsync());

        Assert.Null(transport.CurrentResourceProfile);
        Assert.Equal(
            RuntimeResourcePolicies.Standard.MaximumRpcValueBytes,
            transport.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
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
                RuntimeProtocolFeatures.SchemaFirstRpcV2,
                RuntimeProtocolFeatures.RpcPermissionsV2,
            ],
        };
        var key = new RuntimeRpcPermissionKey(
            "caller.package",
            "1.0.0",
            new string('a', 64),
            "runtime",
            "example.rpc",
            "1.0.0",
            new string('b', 64),
            "invoke");
        var permission = new RuntimeRpcPermissionDescriptor(
            key,
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
        Assert.Equal(5, (await client.GrantRpcPermissionAsync(key)).Revision);
        Assert.Equal(6, (await client.RevokeRpcPermissionAsync(key)).Revision);
        Assert.Contains(handler.PathAndQueries, path =>
            path.EndsWith("afterRevision=2&afterSequence=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppRpcInvariantReport_PostsTypedExactEndpointRequest()
    {
        RuntimeRpcAppInvariantViolationRequest? captured = null;
        var handshake = CreateHandshake() with
        {
            SupportedFeatures =
            [
                RuntimeProtocolFeatures.VersionedApiV1,
                RuntimeProtocolFeatures.AppWebRpcV2,
            ],
        };
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(handshake),
            "/api/v1/rpc/app/invariant-violation" => Json(
                new RuntimeRpcAppInvariantViolationResponse(true, null)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/rpc/app/invariant-violation")
            {
                captured = request.Content!.ReadFromJsonAsync<RuntimeRpcAppInvariantViolationRequest>()
                    .GetAwaiter()
                    .GetResult();
            }
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        var request = new RuntimeRpcAppInvariantViolationRequest(
            "app-session",
            "rpc1_exact_provider",
            "line one\r\nline two " + new string('x', 600),
            "scope-one");

        var result = await client.TryReportAppRpcInvariantViolationAsync(request);

        Assert.True(result.Accepted);
        Assert.NotNull(captured);
        Assert.Equal(request.SessionId, captured.SessionId);
        Assert.Equal(request.EndpointReference, captured.EndpointReference);
        Assert.Equal(request.CallScopeId, captured.CallScopeId);
        Assert.Equal(512, captured.ExceptionMessage.Length);
        Assert.DoesNotContain('\r', captured.ExceptionMessage);
        Assert.DoesNotContain('\n', captured.ExceptionMessage);
        Assert.Equal(
            ["/api/handshake", "/api/v1/rpc/app/invariant-violation"],
            handler.Paths);
    }

    [Fact]
    public async Task UninstallClient_PreflightsAndCommitsExactPlanToken()
    {
        var changeSet = new PackageLifecycleChangeSet(["demo package"], ["demo package"], ["demo package"], ["demo package"], false);
        var plan = new PackageUninstallPlan(
            "demo package",
            [new PackageUninstallPlanPackage("demo package", "Demo", "1.0.0")],
            [],
            ["demo package"],
            changeSet,
            PackageUninstallDataBehavior.Retain,
            ["demo package"],
            new string('a', 64));
        PackageUninstallRequest? posted = null;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/packages/demo%20package/uninstall-plan" => Json(plan),
            "/api/v1/packages/demo%20package/uninstall" => Json(new PackageOperationResult(
                true,
                "Uninstalled.",
                true,
                false,
                [],
                [])
            {
                ChangeSet = changeSet,
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/uninstall", StringComparison.Ordinal))
            {
                posted = request.Content!.ReadFromJsonAsync<PackageUninstallRequest>()
                    .GetAwaiter()
                    .GetResult();
            }
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var receivedPlan = await client.GetPackageUninstallPlanAsync("demo package");
        var result = await client.UninstallPackageAsync(
            "demo package",
            new PackageUninstallRequest(false, receivedPlan.ConfirmationToken));

        Assert.True(result.Success);
        Assert.Equal(receivedPlan.ConfirmationToken, posted?.ConfirmationToken);
        Assert.False(posted?.AllowCascade);
        Assert.Equal(
            [
                "/api/handshake",
                "/api/v1/packages/demo%20package/uninstall-plan",
                "/api/v1/packages/demo%20package/uninstall",
            ],
            handler.Paths);
    }

    [Fact]
    public async Task PackageSettingsClient_UsesTypedEscapedValueEndpoints()
    {
        SetPackageSettingValueRequest? posted = null;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/packages/demo%20package/settings/api%20key" when request.Method == HttpMethod.Get
                => Json(new PackageSettingValueResponse(true, "stored", "effective")),
            "/api/v1/packages/demo%20package/settings/api%20key" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }, request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                posted = request.Content!.ReadFromJsonAsync<SetPackageSettingValueRequest>()
                    .GetAwaiter()
                    .GetResult();
            }
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        var value = await client.GetPackageSettingValueAsync("demo package", "api key");
        await client.SetPackageSettingValueAsync("demo package", "api key", "new-value");
        await client.DeletePackageSettingValueAsync("demo package", "api key");

        Assert.True(value.IsStored);
        Assert.Equal("new-value", posted?.Value);
        Assert.Equal(
            [
                "/api/handshake",
                "/api/v1/packages/demo%20package/settings/api%20key",
                "/api/v1/packages/demo%20package/settings/api%20key",
                "/api/v1/packages/demo%20package/settings/api%20key",
            ],
            handler.Paths);
    }

    [Fact]
    public async Task StackImportPlanDiscard_IsEscapedAndIdempotentForMissingPlans()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/stacks/import/plans/plan%20one" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        await client.DiscardStackImportPlanAsync("plan one");

        Assert.Equal(
            ["/api/handshake", "/api/v1/stacks/import/plans/plan%20one"],
            handler.Paths);
    }

    [Fact]
    public async Task PackageAuthSessionCancel_UsesTheBoundedCallbackSessionEndpoint()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/handshake" => Json(CreateHandshake()),
            "/api/v1/packages/demo%20package/callbacks/sessions/auth%20one" => Json(true),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);

        Assert.True(await client.CancelPackageAuthSessionAsync("demo package", "auth one"));
        Assert.Equal(
            ["/api/handshake", "/api/v1/packages/demo%20package/callbacks/sessions/auth%20one"],
            handler.Paths);
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Ndjson(params object[] frames)
    {
        var payload = string.Join(
            '\n',
            frames.Select(static frame => JsonSerializer.Serialize(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            + "\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload)),
        };
    }

    private static HttpResponseMessage RawInvokeResponse(JsonElement value)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                $"{{\"value\":{value.GetRawText()},\"error\":null}}")),
        };

    private static RuntimeRpcAppInvokeRequest Invocation(JsonElement request)
        => new(
            "app-session",
            "rpc1_provider",
            "example",
            "invoke",
            request,
            DeadlineUtc: null);

    private static JsonElement StringWithSerializedSize(int byteCount, bool escapedQuotes)
    {
        if (byteCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }
        var escapedCharacterBytes = JsonSerializer.SerializeToUtf8Bytes("\"").Length - 2;
        var escapedCharacters = escapedQuotes ? 1 : 0;
        var plainCharacters = byteCount - 2 - escapedCharacters * escapedCharacterBytes;
        if (plainCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }
        var value = JsonSerializer.SerializeToElement(
            (escapedQuotes ? "\"" : string.Empty) + new string('x', plainCharacters));
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) != byteCount)
        {
            throw new InvalidOperationException("The test RPC value did not serialize to the requested size.");
        }
        return value;
    }

    private static JsonElement NestedArray(int depth)
    {
        if (depth < 1 || depth > 72)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }
        using var document = JsonDocument.Parse(
            new string('[', depth - 1) + "0" + new string(']', depth - 1),
            new JsonDocumentOptions { MaxDepth = 72 });
        return document.RootElement.Clone();
    }

    private static RuntimeResourcePolicy CreateResourcePolicy(
        int maximumRpcValueBytes,
        int maximumValueDepth)
        => new(
            maximumRpcValueBytes,
            maximumValueDepth,
            RuntimeResourcePolicies.CustomMinimums.MaximumValueNodes,
            RuntimeResourcePolicies.CustomMinimums.MaximumObjectMembers,
            RuntimeResourcePolicies.CustomMinimums.MaximumInlineDecodedBytes,
            RuntimeResourcePolicies.CustomMinimums.MaximumContentReferenceBytes);

    private static RuntimeHandshakeResponse CreateResourceHandshake(RuntimeResourcePolicy policy)
    {
        var hash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(policy);
        return new RuntimeHandshakeResponse(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            [
                RuntimeProtocolFeatures.VersionedApiV1,
                RuntimeProtocolFeatures.AppWebRpcV2,
                RuntimeProtocolFeatures.RuntimeResourcePolicyV1,
            ],
            new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "Development", "Development"),
            new RuntimeResourceProfileDescriptor(
                hash,
                policy.MaximumRpcValueBytes,
                policy.MaximumValueDepth,
                policy.MaximumValueNodes,
                policy.MaximumObjectMembers,
                policy.MaximumInlineDecodedBytes,
                policy.MaximumContentReferenceBytes));
    }

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
