using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppWebBridgeTests
{
    [Fact]
    public async Task Invoke_UsesCallerDescriptorForAbiCompatibleAnnotationDifferentProvider()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            var callerBytes = RpcDescriptorBytes("Caller request annotation.");
            var providerBytes = RpcDescriptorBytes("Provider request annotation.");
            var callerDescriptor = SunderRpcContractDescriptor.Parse(callerBytes);
            var providerDescriptor = SunderRpcContractDescriptor.Parse(providerBytes);
            Assert.Equal(callerDescriptor.ContractId, providerDescriptor.ContractId);
            Assert.Equal(callerDescriptor.Version, providerDescriptor.Version);
            Assert.Equal(callerDescriptor.AbiSha256, providerDescriptor.AbiSha256);
            Assert.NotEqual(callerDescriptor.Sha256, providerDescriptor.Sha256);

            var catalog = WriteContractCatalog(root, callerBytes, callerDescriptor);
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            var rpc = new InvocationRpcClient(providerDescriptor);
            await using var bridge = new AppWebBridge(
                webView,
                server,
                rpc,
                catalog,
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            var expectedBootstrap = AppWebBootstrapScriptGenerator.Generate(
                AppWebBridge.Protocol,
                AppWebBridge.ProtocolVersion,
                origin,
                bridge.SessionNonce,
                RuntimeClientResourceLimits.Standard);
            Assert.Equal(
                expectedBootstrap,
                AppWebBootstrapScriptGenerator.Generate(
                    AppWebBridge.Protocol,
                    AppWebBridge.ProtocolVersion,
                    origin,
                    bridge.SessionNonce,
                    RuntimeClientResourceLimits.Standard));
            await bridge.EstablishAsync(server.BaseUri);
            var bootstrap = Assert.Single(webView.Scripts);
            Assert.Equal(expectedBootstrap, bootstrap);
            Assert.Contains(
                "if (details !== undefined) error.details = freezeJson(details);",
                bootstrap,
                StringComparison.Ordinal);
            Assert.Contains("detail.kind !== 'domain'", bootstrap, StringComparison.Ordinal);
            Assert.Contains("const encoder = new TextEncoder();", bootstrap, StringComparison.Ordinal);
            Assert.Contains("rpcValueBytes: 33554432", bootstrap, StringComparison.Ordinal);
            Assert.Contains("valueDepth: 64", bootstrap, StringComparison.Ordinal);
            Assert.Contains("pending.size >= maxPendingRequests", bootstrap, StringComparison.Ordinal);
            Assert.Contains("state.timer = setTimeout", bootstrap, StringComparison.Ordinal);
            Assert.Contains("clearTimeout(state.timer)", bootstrap, StringComparison.Ordinal);
            Assert.Contains("pending.delete(state.requestId)", bootstrap, StringComparison.Ordinal);
            Assert.Contains("queuedEventBytes + emittedBytes", bootstrap, StringComparison.Ordinal);
            Assert.Contains("resourceProfile,", bootstrap, StringComparison.Ordinal);
            Assert.True(
                bootstrap.IndexOf("encoder.encode(body).byteLength", StringComparison.Ordinal)
                < bootstrap.IndexOf("pending.set(requestId, state)", StringComparison.Ordinal));
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_discover",
                "rpc.discover",
                new { contractId = callerDescriptor.ContractId }));
            var discovery = ReadResponse(Assert.Single(webView.Scripts));
            var discoveredProvider = Assert.Single(
                discovery.GetProperty("value").GetProperty("providers").EnumerateArray().ToArray());
            Assert.Equal(providerDescriptor.Sha256, discoveredProvider.GetProperty("descriptorSha256").GetString());
            Assert.Equal(providerDescriptor.AbiSha256, discoveredProvider.GetProperty("abiSha256").GetString());
            Assert.False(discoveredProvider.TryGetProperty("contractSha256", out _));
            Assert.Equal(rpc.ActivationId, discoveredProvider.GetProperty("activationId").GetGuid());
            Assert.Equal(1, discoveredProvider.GetProperty("activationEpoch").GetInt64());
            Assert.Equal(1, discoveredProvider.GetProperty("sessionGeneration").GetInt64());
            var providerHandle = discoveredProvider.GetProperty("providerHandle").GetString();
            Assert.NotNull(providerHandle);
            Assert.Equal(providerHandle, discoveredProvider.GetProperty("endpointReference").GetString());
            Assert.DoesNotContain(InvocationRpcClient.EndpointReference, webView.Scripts.Single(), StringComparison.Ordinal);

            // Portable locator metadata does not grant document invocation authority.
            // Only the receiver-local, nonce/document-bound handle can dispatch.
            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(bridge.SessionNonce, origin, "b_raw_endpoint", "rpc.invoke", new
            {
                providerHandle = InvocationRpcClient.EndpointReference,
                serviceId = "example", methodId = "ping", request = new { message = "forged handle" },
            }));
            var rawEndpoint = ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error");
            Assert.Equal("rpc.bridge.provider-handle", rawEndpoint.GetProperty("code").GetString());
            Assert.Equal("not-dispatched", rawEndpoint.GetProperty("dispatchState").GetString());
            Assert.Equal(0, rpc.InvokeCalls);

            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_invalid",
                "rpc.invoke",
                new
                {
                    providerHandle,
                    serviceId = "example",
                    methodId = "ping",
                    request = new { message = 42 },
                }));
            var invalidResponse = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("rpc.bridge.request-schema", invalidResponse.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("not-dispatched", invalidResponse.GetProperty("error").GetProperty("dispatchState").GetString());
            Assert.Equal("safe", invalidResponse.GetProperty("error").GetProperty("retrySafety").GetString());
            Assert.Equal(0, rpc.InvokeCalls);

            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_invoke",
                "rpc.invoke",
                new
                {
                    providerHandle,
                    serviceId = "example",
                    methodId = "ping",
                    request = new { message = "hello" },
                }));

            var invocation = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("result", invocation.GetProperty("type").GetString());
            Assert.Equal("received hello", invocation.GetProperty("value").GetProperty("reply").GetString());
            Assert.Equal(1, rpc.InvokeCalls);
            Assert.Equal(InvocationRpcClient.EndpointReference, rpc.LastEndpointReference);

            rpc.Error = new SunderRpcError(
                SunderRpcErrorKind.Domain,
                "example.rejected",
                "Rejected.",
                SunderRpcDispatchState.PossiblyDispatched,
                SunderRpcRetrySafety.Unsafe,
                JsonSerializer.SerializeToElement(new { reason = "duplicate" }));
            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_error_details",
                "rpc.invoke",
                new
                {
                    providerHandle,
                    serviceId = "example",
                    methodId = "ping",
                    request = new { message = "hello" },
                }));
            var domainError = ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error");
            Assert.Equal("domain", domainError.GetProperty("kind").GetString());
            Assert.Equal("duplicate", domainError.GetProperty("details").GetProperty("reason").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Navigation_RotatesTrustAndDoesNotLetLateResponsesRemoveNewDocumentRequests()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            var rpc = new BlockingRpcClient();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                rpc,
                AppWebRpcContractCatalog.Load(
                    root,
                    new SunderPackageManifest { ContractBundles = [] },
                    new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, [])),
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);

            await bridge.NavigationStartingAsync();
            var firstNonce = bridge.SessionNonce;
            await bridge.HandleMessageAsync(Request(firstNonce, origin, "b_0", "rpc.discover", new { contractId = "example.rpc" }));
            Assert.Equal(0, rpc.DiscoverCalls);

            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();
            var firstRequest = bridge.HandleMessageAsync(
                Request(firstNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await rpc.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await bridge.NavigationStartingAsync();
            var secondNonce = bridge.SessionNonce;
            Assert.NotEqual(firstNonce, secondNonce);
            await bridge.EstablishAsync(server.GetRouteUri("/workspace"));
            webView.ClearScripts();
            var secondRequest = bridge.HandleMessageAsync(
                Request(secondNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await rpc.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await bridge.HandleMessageAsync(Request(firstNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await bridge.HandleMessageAsync($$"""{"nonce":"{{firstNonce}}","requestId":"b_1","broken":""");
            Assert.Empty(webView.Scripts); // Old authentication/parse failures cannot reject the new b_1.
            rpc.FirstRelease.TrySetResult();
            await firstRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, bridge.PendingRequestCount);
            Assert.Equal(0, bridge.ProviderHandleCount); // Late discovery must not repopulate the new document's handles.
            Assert.Empty(webView.Scripts);

            rpc.SecondRelease.TrySetResult();
            await secondRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, bridge.PendingRequestCount);
            Assert.Equal(1, bridge.ProviderHandleCount);
            Assert.Single(webView.Scripts);
            Assert.Contains("b_1", webView.Scripts[0], StringComparison.Ordinal);
            Assert.Contains("providerHandle", webView.Scripts[0], StringComparison.Ordinal);
            Assert.DoesNotContain(BlockingRpcClient.SecretEndpoint, webView.Scripts[0], StringComparison.Ordinal);

            await bridge.HandleMessageAsync(
                Request(secondNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await bridge.HandleMessageAsync(
                Request(firstNonce, origin, "b_2", "rpc.discover", new { contractId = "example.rpc" }));
            Assert.Equal(2, rpc.DiscoverCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("disposal")]
    [InlineData("live")]
    public async Task CancelledReplyNativeFailureIsDiscardedOnlyAfterItsExactDocumentRetires(string boundary)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            var rpc = new DeadlineRpcClient();
            await using var bridge = new AppWebBridge(webView, server, rpc, EmptyCatalog(root), new ExternalBrowserService(), _ => { });
            await bridge.EstablishAsync(server.BaseUri);
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            var originalNonce = bridge.SessionNonce;
            var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseAttempts = 0;
            webView.EvaluateScript = script =>
            {
                if (script.StartsWith("window.__sunderReceive?.(", StringComparison.Ordinal)
                    && ReadResponse(script).GetProperty("requestId").GetString() == "b_1"
                    && Interlocked.Increment(ref responseAttempts) == 1)
                {
                    sendStarted.TrySetResult();
                    return nativeResult.Task;
                }
                return Task.FromResult<string?>("true");
            };
            using var caller = new CancellationTokenSource();
            using var replacementCaller = new CancellationTokenSource();
            var request = bridge.HandleMessageAsync(Request(originalNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }), caller.Token);
            Task? replacement = null;
            try
            {
                await rpc.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (boundary == "navigation")
                {
                    await bridge.NavigationStartingAsync();
                    Assert.NotEqual(originalNonce, bridge.SessionNonce);
                    await bridge.EstablishAsync(server.GetRouteUri("/workspace"));
                    replacement = bridge.HandleMessageAsync(Request(bridge.SessionNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }), replacementCaller.Token);
                    Assert.Equal(1, bridge.PendingRequestCount);
                }
                else if (boundary == "disposal") await bridge.DisposeAsync();

                var failure = new InvalidOperationException("Unable to invoke script before any page was loaded.");
                nativeResult.SetException(failure);
                if (boundary == "live")
                    Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(5))));
                else await request.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, responseAttempts); // Neither replay the old send nor retarget its terminal response.
                if (replacement is not null)
                {
                    Assert.False(replacement.IsCompleted);
                    Assert.Equal(1, bridge.PendingRequestCount);
                }
            }
            finally
            {
                nativeResult.TrySetResult("true");
                caller.Cancel();
                replacementCaller.Cancel();
                if (replacement is not null) await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "LoopbackNetwork")]
    [Trait("Size", "Medium")]
    public async Task Admission_RejectsWrongNonceAndOriginBeforeCreatingPendingRequest()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            var rpc = new BlockingRpcClient();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                rpc,
                EmptyCatalog(root),
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                "wrong-nonce",
                origin,
                "b_wrong_nonce",
                "rpc.discover",
                new { contractId = "example.rpc" }));
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                "https://forged.example",
                "b_wrong_origin",
                "rpc.discover",
                new { contractId = "example.rpc" }));

            Assert.Equal(0, rpc.DiscoverCalls);
            Assert.Equal(0, bridge.PendingRequestCount);
            Assert.Single(webView.Scripts); // A foreign nonce never receives a current-document response.
            Assert.All(webView.Scripts, script => Assert.Equal(
                "rpc.bridge.authentication",
                ReadResponse(script).GetProperty("error").GetProperty("code").GetString()));
            webView.ClearScripts();

            var admitted = bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_admitted",
                "rpc.discover",
                new { contractId = "example.rpc" }));
            await rpc.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, bridge.PendingRequestCount);
            rpc.FirstRelease.TrySetResult();
            await admitted.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, bridge.PendingRequestCount);
            Assert.Single(webView.Scripts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("sunder.bridge.unknown", AppWebBridge.ProtocolVersion)]
    [InlineData(AppWebBridge.Protocol, 99)]
    public async Task Admission_RejectsUnknownProtocolIdentityOrVersionWithoutFallback(
        string protocol,
        int version)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root),
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();

            await bridge.HandleMessageAsync(JsonSerializer.Serialize(new
            {
                protocol,
                version,
                nonce = bridge.SessionNonce,
                origin,
                requestId = "b_unknown_protocol",
                method = "navigation.getState",
                deadlineUtc = (string?)null,
                payload = new { },
            }));

            var rejection = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal(AppWebBridge.Protocol, rejection.GetProperty("protocol").GetString());
            Assert.Equal(AppWebBridge.ProtocolVersion, rejection.GetProperty("version").GetInt32());
            Assert.Equal(
                "rpc.bridge.request-envelope",
                rejection.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserDeadline_IsCarriedForEveryCallAndCompletesAsAuthenticatedDeadlineExceeded()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            var rpc = new DeadlineRpcClient();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                rpc,
                AppWebRpcContractCatalog.Load(
                    root,
                    new SunderPackageManifest { ContractBundles = [] },
                    new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, [])),
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            var bootstrap = Assert.Single(webView.Scripts);
            Assert.Contains("deadlineUtc: options?.deadline?.toISOString?.() ?? null", bootstrap, StringComparison.Ordinal);
            webView.ClearScripts();

            var request = bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_deadline",
                "rpc.discover",
                new { contractId = "example.rpc" },
                DateTimeOffset.UtcNow.AddMilliseconds(300)));
            await rpc.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await request.WaitAsync(TimeSpan.FromSeconds(5));

            var response = Assert.Single(webView.Scripts);
            Assert.Contains("deadline-exceeded", response, StringComparison.Ordinal);
            Assert.Contains("rpc.call.deadline-exceeded", response, StringComparison.Ordinal);
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverableInvalidAndOversizedMessages_ReturnTerminalErrors()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            var limits = ResourceLimits();
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root),
                new ExternalBrowserService(),
                _ => { },
                limits);
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();

            var malformed = $$"""
                {"requestId":"b_malformed","nonce":"{{bridge.SessionNonce}}","protocol":"{{AppWebBridge.Protocol}}","broken":
                """;
            await bridge.HandleMessageAsync(malformed);

            var malformedError = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("b_malformed", malformedError.GetProperty("requestId").GetString());
            Assert.Equal("error", malformedError.GetProperty("type").GetString());
            webView.ClearScripts();

            var oversized = Request(
                bridge.SessionNonce,
                origin,
                "b_oversized",
                "rpc.discover",
                new { contractId = new string('x', limits.MaximumJsonEnvelopeBytes) });
            await bridge.HandleMessageAsync(oversized);

            var sizeError = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("b_oversized", sizeError.GetProperty("requestId").GetString());
            Assert.Equal(
                "rpc.bridge.request-envelope-limit",
                sizeError.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeRequestPreflight_UsesExactEscapedUtf8BytesAndValueDepth()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            var limits = ResourceLimits();
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root),
                new ExternalBrowserService(),
                _ => { },
                limits);
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_exact",
                "rpc.invoke",
                InvocationPayload(JsonStringWithSize(limits.EffectivePolicy.MaximumRpcValueBytes, escaped: true))));
            Assert.Equal(
                "rpc.bridge.provider-handle",
                ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error").GetProperty("code").GetString());
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_plus_one",
                "rpc.invoke",
                InvocationPayload(JsonStringWithSize(limits.EffectivePolicy.MaximumRpcValueBytes + 1, escaped: true))));
            Assert.Equal(
                "rpc.bridge.payload-limit",
                ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error").GetProperty("code").GetString());
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_depth_exact",
                "rpc.invoke",
                InvocationPayload(NestedArray(limits.EffectivePolicy.MaximumValueDepth))));
            Assert.Equal(
                "rpc.bridge.provider-handle",
                ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error").GetProperty("code").GetString());
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_depth_plus_one",
                "rpc.invoke",
                InvocationPayload(NestedArray(limits.EffectivePolicy.MaximumValueDepth + 1))));
            Assert.Equal(
                "rpc.bridge.payload-limit",
                ReadResponse(Assert.Single(webView.Scripts)).GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeResultPreflight_AllowsExactFinalEnvelopeValueAndRejectsPlusOne()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            var descriptorBytes = RpcDescriptorBytes("Resource request.", responseMaxLength: 1_000_000);
            var descriptor = SunderRpcContractDescriptor.Parse(descriptorBytes);
            var catalog = WriteContractCatalog(root, descriptorBytes, descriptor);
            var limits = ResourceLimits();
            var rpc = new ResourceRpcClient(descriptor, limits);
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                rpc,
                catalog,
                new ExternalBrowserService(),
                _ => { });
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();

            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_discover_limits",
                "rpc.discover",
                new { contractId = descriptor.ContractId }));
            var providerHandle = ReadResponse(Assert.Single(webView.Scripts))
                .GetProperty("value")
                .GetProperty("providers")[0]
                .GetProperty("providerHandle")
                .GetString();
            webView.ClearScripts();

            rpc.Response = ReplyObjectWithSize(limits.EffectivePolicy.MaximumRpcValueBytes);
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_result_exact",
                "rpc.invoke",
                new
                {
                    providerHandle,
                    serviceId = "example",
                    methodId = "ping",
                    request = new { message = "hello" },
                }));
            var exact = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("result", exact.GetProperty("type").GetString());
            Assert.Equal(
                limits.EffectivePolicy.MaximumRpcValueBytes,
                Encoding.UTF8.GetByteCount(exact.GetProperty("value").GetRawText()));
            webView.ClearScripts();

            rpc.Response = ReplyObjectWithSize(limits.EffectivePolicy.MaximumRpcValueBytes + 1);
            await bridge.HandleMessageAsync(Request(
                bridge.SessionNonce,
                origin,
                "b_result_plus_one",
                "rpc.invoke",
                new
                {
                    providerHandle,
                    serviceId = "example",
                    methodId = "ping",
                    request = new { message = "hello" },
                }));
            var rejected = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("error", rejected.GetProperty("type").GetString());
            Assert.Equal(
                "rpc.bridge.payload-limit",
                rejected.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task Establish_RequiresPositiveBootstrapAcknowledgementAndRetiresFailedDocument(string? result)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView { EvaluateScript = _ => Task.FromResult(result) };
            await using var bridge = new AppWebBridge(webView, server, UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root), new ExternalBrowserService(), _ => { });
            await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.EstablishAsync(server.BaseUri));
            Assert.Equal(2, webView.Scripts.Count);
            var retired = ReadResponse(webView.Scripts[1]);
            Assert.Equal("retired", retired.GetProperty("type").GetString());
            Assert.Equal(bridge.SessionNonce, retired.GetProperty("nonce").GetString());
            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(bridge.SessionNonce, server.Origin.GetLeftPart(UriPartial.Authority),
                "b_after_failed_bootstrap", "navigation.getState", new { }));
            Assert.Empty(webView.Scripts);
            Assert.Equal(0, bridge.PendingRequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Establish_LateCompletionCannotRetireReplacementDocument(string oldResult)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            var oldEvaluation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var webView = new TestWebView { EvaluateScript = _ => oldEvaluation.Task };
            await using var bridge = new AppWebBridge(webView, server, UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root), new ExternalBrowserService(), _ => { });
            var oldNonce = bridge.SessionNonce;
            var oldEstablishment = bridge.EstablishAsync(server.BaseUri);
            webView.EvaluateScript = null;
            await bridge.NavigationStartingAsync();
            await bridge.EstablishAsync(server.GetRouteUri("/workspace"));
            var currentNonce = bridge.SessionNonce;
            webView.ClearScripts();
            oldEvaluation.SetResult(oldResult);
            if (oldResult == "true") await oldEstablishment;
            else await Assert.ThrowsAsync<InvalidOperationException>(() => oldEstablishment);
            var retirement = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal("retired", retirement.GetProperty("type").GetString());
            Assert.Equal(oldNonce, retirement.GetProperty("nonce").GetString());
            webView.ClearScripts();
            await bridge.HandleMessageAsync(Request(currentNonce, server.Origin.GetLeftPart(UriPartial.Authority),
                "b_1", "navigation.getState", new { }));
            var response = ReadResponse(Assert.Single(webView.Scripts));
            Assert.Equal(currentNonce, response.GetProperty("nonce").GetString());
            Assert.Equal("/workspace", response.GetProperty("value").GetProperty("route").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ViewRetirement_CancelsOnlyItsRequestsAndPreservesSharedActivationClient()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var firstView = new TestWebView();
            using var secondView = new TestWebView();
            var rpc = new BlockingRpcClient();
            await using var first = new AppWebBridge(firstView, server, new NonDisposingAppWebRpcClient(rpc),
                EmptyCatalog(root), new ExternalBrowserService(), _ => { });
            await using var second = new AppWebBridge(secondView, server, new NonDisposingAppWebRpcClient(rpc),
                EmptyCatalog(root), new ExternalBrowserService(), _ => { });
            await first.EstablishAsync(server.BaseUri);
            await second.EstablishAsync(server.BaseUri);
            firstView.ClearScripts();
            secondView.ClearScripts();
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);
            var firstCall = first.HandleMessageAsync(Request(first.SessionNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            var secondCall = second.HandleMessageAsync(Request(second.SessionNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await Task.WhenAll(rpc.FirstStarted.Task, rpc.SecondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await first.DisposeAsync();
            Assert.True(rpc.FirstToken.IsCancellationRequested);
            Assert.False(rpc.SecondToken.IsCancellationRequested);
            Assert.Equal(0, rpc.DisposeCalls);
            Assert.Equal(0, first.PendingRequestCount);
            Assert.Equal(1, second.PendingRequestCount);
            Assert.Equal("retired", ReadResponse(Assert.Single(firstView.Scripts)).GetProperty("type").GetString());
            Assert.Empty(secondView.Scripts);
            rpc.FirstRelease.SetResult();
            rpc.SecondRelease.SetResult();
            await Task.WhenAll(firstCall, secondCall).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("result", ReadResponse(Assert.Single(secondView.Scripts)).GetProperty("type").GetString());
            await second.DisposeAsync();
            Assert.Equal(0, rpc.DisposeCalls);
            await rpc.DisposeAsync();
            Assert.Equal(1, rpc.DisposeCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ViewDisposal_NotifiesDocumentBeforeDetachingOrDisposingWebView()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            // Preserve control-thread affinity across the deliberately asynchronous cleanup.
            // Do not borrow Dispatcher.UIThread: another parallel test may own that singleton.
            var previousContext = SynchronizationContext.Current;
            using var context = new TestSupport.SingleThreadSynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var verification = VerifyDisposalAsync();
                context.RunUntilCompleted(verification);
                await verification;
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

            async Task VerifyDisposalAsync()
            {
                using var webView = new TestWebView();
                using var view = new AppWebPackageViewControl(server.BaseUri, root, server, new TestWebViewFactory(webView),
                    UnavailableAppWebRpcClient.Instance, EmptyCatalog(root), new ExternalBrowserService());
                await view.Bridge.EstablishAsync(server.BaseUri);
                var notification = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                webView.ClearScripts();
                webView.EvaluateScript = script =>
                {
                    Assert.False(webView.Disposed.Task.IsCompleted);
                    Assert.Same(webView.Control, view.Content);
                    Assert.Equal("retired", ReadResponse(script).GetProperty("type").GetString());
                    return notification.Task;
                };
                webView.OnDispose = () => Assert.Null(view.Content);
                view.Dispose();
                view.Dispose();
                Assert.Single(webView.Scripts);
                Assert.False(webView.Disposed.Task.IsCompleted);
                notification.SetResult("true");
                await webView.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_APP_WEB_BRIDGE_VM")]
    [Trait("Category", "Conformance")]
    [Trait("Capability", "Process")]
    [Trait("Size", "Medium")]
    public async Task BootstrapJavaScriptCrossLanguageVm_ExecutesExactNativeContractAcrossNavigation()
    {
        await RunBootstrapVmOracleAsync(static script => script);
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_APP_WEB_BRIDGE_VM")]
    [Trait("Category", "Mutation")]
    [Trait("Capability", "Process")]
    [Trait("Size", "Medium")]
    public async Task BootstrapJavaScriptCrossLanguageVm_RejectsSeededNonceOwnershipMutation()
    {
        var exception = await Assert.ThrowsAsync<AppWebBridgeVmOracleException>(() =>
            RunBootstrapVmOracleAsync(static script =>
            {
                const string guard = "message?.nonce !== nonce";
                var mutated = script.Replace(guard, "false", StringComparison.Ordinal);
                Assert.NotEqual(script, mutated);
                return mutated;
            }));

        Assert.Contains(
            "current document accepted a prior-document response",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static async Task RunBootstrapVmOracleAsync(Func<string, string> adaptSecondBootstrap)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head></head></html>");
        try
        {
            await using var server = await AppWebContentServer.StartAsync(root, Target(), CancellationToken.None);
            using var webView = new TestWebView();
            await using var bridge = new AppWebBridge(
                webView,
                server,
                UnavailableAppWebRpcClient.Instance,
                EmptyCatalog(root),
                new ExternalBrowserService(),
                _ => Assert.Fail("The VM oracle must not accept its intentionally invalid route."));
            var origin = server.Origin.GetLeftPart(UriPartial.Authority);

            await bridge.EstablishAsync(server.BaseUri);
            var firstBootstrap = Assert.Single(webView.Scripts);
            var firstNonce = bridge.SessionNonce;
            webView.ClearScripts();
            var firstResult = await CaptureVmResponseAsync(
                bridge,
                webView,
                Request(firstNonce, origin, "b_1", "navigation.getState", new { }));
            var firstError = await CaptureVmResponseAsync(
                bridge,
                webView,
                Request(firstNonce, origin, "b_2", "navigation.navigate", new { route = "not-a-route" }));
            var firstPending = await CaptureVmResponseAsync(
                bridge,
                webView,
                Request(firstNonce, origin, "b_3", "navigation.getState", new { }));
            var firstLateTarget = await CaptureVmResponseAsync(
                bridge,
                webView,
                Request(firstNonce, origin, "b_4", "navigation.getState", new { }));

            await bridge.NavigationStartingAsync();
            var firstRetirement = new BridgeVmResponse(Assert.Single(webView.Scripts), ReadResponse(Assert.Single(webView.Scripts)));
            webView.ClearScripts();
            var secondNonce = bridge.SessionNonce;
            Assert.NotEqual(firstNonce, secondNonce);
            await bridge.EstablishAsync(server.GetRouteUri("/workspace"));
            var secondBootstrap = adaptSecondBootstrap(Assert.Single(webView.Scripts));
            webView.ClearScripts();
            var secondResult = await CaptureVmResponseAsync(
                bridge,
                webView,
                Request(secondNonce, origin, "b_1", "navigation.getState", new { }));
            var wrongNonceForFirstPending = RewriteVmResponseNonce(firstPending, secondNonce);

            var harnessPath = Path.Combine(root, "app-web-bridge-vm-oracle.mjs");
            var configurationPath = Path.Combine(root, "app-web-bridge-vm-oracle.json");
            await File.WriteAllTextAsync(harnessPath, AppWebBridgeVmHarness);
            await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(new
            {
                origin,
                firstNonce,
                secondNonce,
                firstBootstrap,
                secondBootstrap,
                firstRetirement,
                responses = new
                {
                    firstResult,
                    firstError,
                    firstPending,
                    firstLateTarget,
                    secondResult,
                    wrongNonceForFirstPending,
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var startInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(configurationPath);
            await using var process = CapturedProcess.Start(startInfo);
            Sunder.Core.TestSupport.CapturedProcessResult processResult;
            try
            {
                processResult = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(20));
            }
            catch (ProcessFixtureException exception)
            {
                throw new AppWebBridgeVmOracleException(exception.Message, exception);
            }

            using var resultDocument = JsonDocument.Parse(processResult.StandardOutput);
            var result = resultDocument.RootElement;
            Assert.Equal("/", result.GetProperty("firstRoute").GetString());
            Assert.Equal("/workspace", result.GetProperty("secondRoute").GetString());
            Assert.Equal("SunderRpcError", result.GetProperty("error").GetProperty("name").GetString());
            Assert.True(JsonElement.DeepEquals(
                firstError.Envelope.GetProperty("error"),
                result.GetProperty("error").GetProperty("detail")));

            var expectedRequests = new[]
            {
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_1", "navigation.getState", new { })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_2", "navigation.navigate", new { route = "not-a-route" })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_3", "navigation.getState", new { })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_4", "navigation.getState", new { })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_5", "navigation.getState", new { })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_6", "rpc.watch", new { afterRevision = 0, afterSequence = 0 })),
                new VmExpectedRequest("first", Request(firstNonce, origin, "b_7", "rpc.watch", new { afterRevision = 0, afterSequence = 0 })),
                new VmExpectedRequest("second", Request(secondNonce, origin, "b_1", "navigation.getState", new { })),
                new VmExpectedRequest("second", Request(secondNonce, origin, "b_2", "navigation.getState", new { })),
                new VmExpectedRequest("second", Request(secondNonce, origin, "b_2", "bridge.cancel", new { targetRequestId = "b_2" })),
            };
            var actualRequests = result.GetProperty("requests").EnumerateArray().ToArray();
            Assert.Equal(expectedRequests.Length, actualRequests.Length);
            for (var index = 0; index < expectedRequests.Length; index++)
            {
                Assert.Equal(expectedRequests[index].Document, actualRequests[index].GetProperty("document").GetString());
                using var expected = JsonDocument.Parse(expectedRequests[index].Body);
                Assert.True(
                    JsonElement.DeepEquals(expected.RootElement, actualRequests[index].GetProperty("body")),
                    $"VM request {index} did not match the native bridge envelope. Actual: {actualRequests[index].GetProperty("raw").GetString()}");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<BridgeVmResponse> CaptureVmResponseAsync(
        AppWebBridge bridge,
        TestWebView webView,
        string request)
    {
        await bridge.HandleMessageAsync(request);
        var script = Assert.Single(webView.Scripts);
        webView.ClearScripts();
        return new BridgeVmResponse(script, ReadResponse(script));
    }

    private static BridgeVmResponse RewriteVmResponseNonce(BridgeVmResponse response, string nonce)
    {
        var root = response.Envelope;
        var json = JsonSerializer.Serialize(new
        {
            protocol = root.GetProperty("protocol").GetString(),
            version = root.GetProperty("version").GetInt32(),
            nonce,
            requestId = root.GetProperty("requestId").GetString(),
            type = root.GetProperty("type").GetString(),
            value = root.GetProperty("value").Clone(),
            error = root.GetProperty("error").Clone(),
        });
        using var document = JsonDocument.Parse(json);
        return new BridgeVmResponse(
            $"window.__sunderReceive?.({JsonSerializer.Serialize(json)});",
            document.RootElement.Clone());
    }

    private sealed record BridgeVmResponse(string Script, JsonElement Envelope);

    private sealed record VmExpectedRequest(string Document, string Body);

    private sealed class AppWebBridgeVmOracleException(string message, Exception innerException)
        : Exception(message, innerException);

    private const string AppWebBridgeVmHarness = """
        import { readFile } from "node:fs/promises";
        import { isDeepStrictEqual } from "node:util";
        import vm from "node:vm";

        const configuration = JSON.parse(await readFile(process.argv[2], "utf8"));
        const requests = [];
        const documents = [];

        function invariant(condition, message) {
          if (!condition) throw new Error(message);
        }

        function exactKeys(value, expected, label) {
          invariant(value !== null && typeof value === "object" && !Array.isArray(value), `${label} must be an object.`);
          const actual = Object.keys(value).sort();
          invariant(isDeepStrictEqual(actual, [...expected].sort()), `${label} keys were ${JSON.stringify(actual)}.`);
        }

        function createDocument(label, script, origin, initialize = true) {
          const timers = new Map();
          const maps = [];
          const events = new EventTarget();
          let timerAllocations = 0;
          class ObservedMap extends Map {
            sets = 0;
            constructor() { super(); maps.push(this); }
            set(key, value) { this.sets++; return super.set(key, value); }
          }
          const sandbox = { TextEncoder, AbortController, Event, CustomEvent, queueMicrotask, Map: ObservedMap,
            addEventListener: events.addEventListener.bind(events),
            removeEventListener: events.removeEventListener.bind(events),
            dispatchEvent: events.dispatchEvent.bind(events),
            setTimeout(callback, delay) {
              timerAllocations++;
              const timer = setTimeout(() => { timers.delete(timer); callback(); }, delay);
              timers.set(timer, true);
              return timer;
            },
            clearTimeout(timer) { timers.delete(timer); clearTimeout(timer); },
          };
          sandbox.window = sandbox;
          sandbox.location = Object.freeze({ origin });
          sandbox.invokeCSharpAction = (raw) => {
            const body = JSON.parse(raw);
            exactKeys(
              body,
              ["protocol", "version", "nonce", "origin", "requestId", "method", "deadlineUtc", "payload"],
              `${label} native request`,
            );
            invariant(body.protocol === "sunder.bridge.v1" && body.version === 1, `${label} request protocol drifted.`);
            invariant(body.origin === configuration.origin, `${label} request origin drifted.`);
            const expectedNonce = label === "second" ? configuration.secondNonce : configuration.firstNonce;
            invariant(body.nonce === expectedNonce, `${label} request nonce drifted.`);
            requests.push({ document: label, raw, body });
          };
          const context = vm.createContext(sandbox, { name: `app-web-bridge-${label}` });
          const compiled = new vm.Script(script, { filename: `app-web-bridge-${label}.js` });
          const document = { label, context, window: sandbox, compiled, initialized: false, maps, timers,
            get timerAllocations() { return timerAllocations; } };
          documents.push(document);
          if (initialize) document.initialized = compiled.runInContext(context);
          return document;
        }

        // The SDK handoff algorithm: subscribe first, then inspect the immutable API.
        // This is deliberately not a polling loop or a second transport implementation.
        function waitForReady(document) {
          return new Promise((resolve, reject) => {
            const cleanup = () => {
              document.window.removeEventListener("sunder:ready", onReady);
              document.window.removeEventListener("pagehide", onHidden);
            };
            const accept = (candidate) => {
              if (candidate === undefined) return;
              cleanup();
              if (candidate !== document.window.sunder || candidate.protocol !== "sunder.bridge.v1"
                  || candidate.version !== 1 || candidate.signal?.aborted !== false) {
                reject(new Error("Bridge is incompatible or retired."));
              } else resolve(candidate);
            };
            const onReady = (event) => accept(event.detail);
            const onHidden = () => { cleanup(); reject(new Error("Document retired before readiness.")); };
            document.window.addEventListener("sunder:ready", onReady);
            document.window.addEventListener("pagehide", onHidden, { once: true });
            accept(document.window.sunder);
          });
        }

        function observedSignal() {
          const controller = new AbortController();
          const signal = controller.signal;
          const listeners = new Set();
          const add = signal.addEventListener.bind(signal);
          const remove = signal.removeEventListener.bind(signal);
          let registrations = 0;
          signal.addEventListener = (type, callback, options) => {
            registrations++;
            listeners.add(callback);
            add(type, callback, options);
          };
          signal.removeEventListener = (type, callback, options) => {
            listeners.delete(callback);
            remove(type, callback, options);
          };
          return { controller, signal, listeners, get registrations() { return registrations; } };
        }

        async function preAbortedCallsAllocateNothing(document) {
          const api = document.window.sunder;
          const observed = observedSignal();
          observed.controller.abort();
          const options = { signal: observed.signal, get deadline() { throw new Error("Pre-aborted call serialized options."); } };
          const payload = { toJSON() { throw new Error("Pre-aborted call serialized its payload."); } };
          const beforeRequests = requests.length;
          const beforeTimers = document.timerAllocations;
          const beforeSets = document.maps.reduce((sum, map) => sum + map.sets, 0);
          const calls = [
            () => api.rpc.discover("example.rpc", options),
            () => api.rpc.invoke("opaque", "service", "method", payload, options),
            () => api.getNavigationState(options),
            () => api.navigate("/workspace", options),
            () => api.openExternal("https://example.test", options),
            () => api.rpc.watch(0, 0, options)[Symbol.asyncIterator]().next(),
            () => api.rpc.subscribe("opaque", "service", "method", payload, options)[Symbol.asyncIterator]().next(),
          ];
          for (const call of calls) {
            const tracked = track(call());
            await tracked.completion.catch(() => {});
            invariant(tracked.state.status === "rejected", "Pre-aborted operation did not reject.");
            const error = tracked.state.error;
            invariant(error.name === "SunderRpcError" && error.error.kind === "cancelled"
              && error.error.code === "rpc.bridge.cancelled" && error.error.dispatchState === "not-dispatched"
              && error.error.retrySafety === "safe", "Pre-aborted failure lost its non-dispatch guarantee.");
          }
          invariant(requests.length === beforeRequests, "Pre-aborted call dispatched native work or cancellation.");
          invariant(document.timerAllocations === beforeTimers, "Pre-aborted call allocated a timer.");
          invariant(document.maps.reduce((sum, map) => sum + map.sets, 0) === beforeSets, "Pre-aborted call allocated pending state.");
          invariant(observed.registrations === 0, "Pre-aborted call registered an abort listener.");
        }

        function validateResponse(response, label) {
          exactKeys(
            response.envelope,
            ["protocol", "version", "nonce", "requestId", "type", "value", "error"],
            `${label} native response`,
          );
          const prefix = "window.__sunderReceive?.(";
          invariant(response.script.startsWith(prefix) && response.script.endsWith(");"), `${label} response script wrapper drifted.`);
          const raw = JSON.parse(response.script.slice(prefix.length, -2));
          invariant(isDeepStrictEqual(JSON.parse(raw), response.envelope), `${label} response script and envelope diverged.`);
        }

        function feed(document, response, label) {
          validateResponse(response, label);
          new vm.Script(response.script, { filename: `${label}.js` }).runInContext(document.context);
        }

        function track(promise) {
          const state = { status: "pending", value: undefined, error: undefined };
          const completion = Promise.resolve(promise).then(
            (value) => {
              state.status = "fulfilled";
              state.value = value;
              return value;
            },
            (error) => {
              state.status = "rejected";
              state.error = error;
              throw error;
            },
          );
          void completion.catch(() => {});
          return { state, completion };
        }

        function begin(document, operation, label) {
          const before = requests.length;
          const tracked = track(operation());
          invariant(requests.length === before + 1, `${label} did not synchronously invoke the native callback exactly once.`);
          return tracked;
        }

        async function settleTurn() {
          await Promise.resolve();
          await Promise.resolve();
          await new Promise((resolve) => setImmediate(resolve));
        }

        async function run() {
          const wrongOrigin = createDocument("wrong-origin", configuration.firstBootstrap, "https://forged.example");
          invariant(wrongOrigin.initialized === false, "Bootstrap admitted the wrong origin.");
          invariant(!Object.prototype.hasOwnProperty.call(wrongOrigin.window, "sunder"), "Wrong-origin bootstrap exposed the API.");

          const missingNative = createDocument("missing-native", configuration.firstBootstrap, configuration.origin, false);
          delete missingNative.window.invokeCSharpAction;
          let missingReady = 0;
          missingNative.window.addEventListener("sunder:ready", () => missingReady++);
          invariant(missingNative.compiled.runInContext(missingNative.context) === false, "Bootstrap admitted a missing native callback.");
          invariant(missingReady === 0 && missingNative.window.sunder === undefined, "Failed bootstrap announced readiness.");
          const abandoned = createDocument("abandoned", configuration.firstBootstrap, configuration.origin, false);
          const abandonedReady = track(waitForReady(abandoned));
          abandoned.window.dispatchEvent(new Event("pagehide"));
          await abandonedReady.completion.catch(() => {});
          invariant(abandonedReady.state.status === "rejected", "Readiness outlived its document.");

          const first = createDocument("first", configuration.firstBootstrap, configuration.origin, false);
          const earlyReady = track(waitForReady(first));
          let readyEvents = 0;
          first.window.addEventListener("sunder:ready", (event) => {
            readyEvents++;
            invariant(event.detail === first.window.sunder && Object.isFrozen(event.detail)
              && event.detail.signal.aborted === false, "Ready event ran before immutable live API installation.");
          });
          invariant(earlyReady.state.status === "pending", "Readiness completed before bootstrap.");
          first.initialized = first.compiled.runInContext(first.context);
          invariant(first.initialized === true, "First bootstrap did not initialize.");
          const firstApi = first.window.sunder;
          invariant(await earlyReady.completion === firstApi, "Early readiness returned a different API.");
          invariant(await waitForReady(first) === firstApi, "Late readiness missed the installed API.");
          invariant(first.compiled.runInContext(first.context) === true, "Idempotent bootstrap did not report success.");
          invariant(readyEvents === 1, "Idempotent bootstrap repeated the ready event.");
          invariant(first.window.sunder === firstApi, "Idempotent bootstrap replaced the API object.");
          invariant(Object.isFrozen(firstApi), "Bridge API is mutable.");
          invariant(Object.isFrozen(firstApi.resourceProfile), "Resource profile is mutable.");
          invariant(Object.isFrozen(firstApi.resourceProfile.effectivePolicy), "Effective resource policy is mutable.");
          await preAbortedCallsAllocateNothing(first);

          const firstCall = begin(first, () => firstApi.getNavigationState(), "first result");
          invariant(firstCall.state.status === "pending", "Unary request did not remain pending before native response.");
          feed(first, configuration.responses.firstResult, "first result");
          const firstValue = await firstCall.completion;
          invariant(firstValue?.route === "/", "First native result shape drifted.");

          const errorCall = begin(first, () => firstApi.navigate("not-a-route"), "native error");
          feed(first, configuration.responses.firstError, "native error");
          let observedError;
          try {
            await errorCall.completion;
          } catch (error) {
            observedError = error;
          }
          invariant(observedError?.name === "SunderRpcError", "Native error did not become SunderRpcError.");
          invariant(
            isDeepStrictEqual(
              JSON.parse(JSON.stringify(observedError.error)),
              configuration.responses.firstError.envelope.error,
            ),
            "Native error detail shape drifted.",
          );
          invariant(Object.isFrozen(observedError.error), "Native error detail is mutable.");

          const ownedPending = begin(first, () => firstApi.getNavigationState(), "owned pending request");
          feed(first, configuration.responses.wrongNonceForFirstPending, "wrong nonce");
          await settleTurn();
          invariant(ownedPending.state.status === "pending", "Wrong-nonce response resolved an owned pending request.");
          feed(first, configuration.responses.firstPending, "owned pending result");
          await ownedPending.completion;

          const lateIsolation = begin(first, () => firstApi.getNavigationState(), "late response target");
          feed(first, configuration.responses.firstPending, "late completed response");
          await settleTurn();
          invariant(lateIsolation.state.status === "pending", "Late response completed a different pending request.");
          feed(first, configuration.responses.firstLateTarget, "late response target result");
          await lateIsolation.completion;

          const ownedSignal = observedSignal();
          const retiringCall = begin(first, () => firstApi.getNavigationState({ signal: ownedSignal.signal }), "retiring unary");
          const retiringStream = firstApi.rpc.watch(0, 0, { signal: ownedSignal.signal })[Symbol.asyncIterator]();
          const retiringNext = track(retiringStream.next());
          const queuedStream = firstApi.rpc.watch(0, 0, { signal: ownedSignal.signal })[Symbol.asyncIterator]();
          first.window.__sunderReceive(JSON.stringify({ protocol: "sunder.bridge.v1", version: 1, nonce: configuration.firstNonce,
            requestId: "b_7", type: "event", value: { sequence: 1 }, error: null }));
          let lifetimeAborts = 0;
          firstApi.signal.addEventListener("abort", () => {
            lifetimeAborts++;
            invariant(first.timers.size === 0 && first.maps.every(map => map.size === 0)
              && ownedSignal.listeners.size === 0, "Lifetime abort preceded pending-work cleanup.");
          });
          feed(first, configuration.firstRetirement, "native retirement");
          feed(first, configuration.firstRetirement, "duplicate native retirement");
          await Promise.all([retiringCall.completion.catch(() => {}), retiringNext.completion.catch(() => {})]);
          invariant(firstApi.signal.aborted && lifetimeAborts === 1, "Native retirement did not abort exactly once.");
          invariant(firstApi.signal.reason.error.code === "rpc.bridge.retired", "Retirement reason lost its structured code.");
          for (const tracked of [retiringCall, retiringNext]) {
            invariant(tracked.state.status === "rejected" && tracked.state.error.error.code === "rpc.bridge.retired"
              && tracked.state.error.error.dispatchState === "possibly-dispatched", "Retirement did not terminate dispatched work.");
          }
          const queuedNext = track(queuedStream.next());
          await queuedNext.completion.catch(() => {});
          invariant(queuedNext.state.status === "rejected", "Retirement delivered a queued old-document event.");
          invariant(first.compiled.runInContext(first.context) === false && readyEvents === 1, "Retired bootstrap became ready again.");
          const lateReady = track(waitForReady(first));
          await lateReady.completion.catch(() => {});
          invariant(lateReady.state.status === "rejected", "Late readiness accepted a retired API.");
          const beforeRetiredCalls = requests.length;
          const afterRetirement = track(firstApi.getNavigationState());
          await afterRetirement.completion.catch(() => {});
          invariant(afterRetirement.state.error?.error.dispatchState === "not-dispatched"
            && requests.length === beforeRetiredCalls && first.timers.size === 0, "Retired API dispatched or retained a new request.");

          const second = createDocument("second", configuration.secondBootstrap, configuration.origin);
          invariant(second.initialized === true, "Rotated bootstrap did not initialize.");
          invariant(configuration.firstNonce !== configuration.secondNonce, "Navigation did not rotate the nonce.");
          const rotated = begin(second, () => second.window.sunder.getNavigationState(), "rotated request");
          feed(second, configuration.responses.firstResult, "prior document response");
          await settleTurn();
          invariant(
            rotated.state.status === "pending",
            "The current document accepted a prior-document response with the same request id.",
          );
          feed(second, configuration.firstRetirement, "late prior-document retirement");
          invariant(!second.window.sunder.signal.aborted, "Prior-document retirement aborted the current document.");
          feed(second, configuration.responses.secondResult, "rotated request result");
          const secondValue = await rotated.completion;
          invariant(secondValue?.route === "/workspace", "Rotated native result shape drifted.");

          const pageHideCall = begin(second, () => second.window.sunder.getNavigationState(), "pagehide request");
          second.window.dispatchEvent(new Event("pagehide"));
          second.window.dispatchEvent(new Event("pagehide"));
          await pageHideCall.completion.catch(() => {});
          invariant(second.window.sunder.signal.aborted && pageHideCall.state.error?.error.code === "rpc.bridge.retired",
            "pagehide did not retire the browser lifetime.");
          invariant(second.timers.size === 0 && second.maps.every(map => map.size === 0), "pagehide retained pending call state.");

          process.stdout.write(JSON.stringify({
            requests,
            firstRoute: firstValue.route,
            secondRoute: secondValue.route,
            error: { name: observedError.name, detail: observedError.error },
          }));
        }

        try {
          await run();
        } catch (error) {
          console.error(`APP_WEB_BRIDGE_VM_ORACLE: ${error?.stack ?? error}`);
          process.exitCode = 1;
        } finally {
          for (const document of documents) for (const timer of document.timers.keys()) clearTimeout(timer);
        }
        """;

    private static string Request(
        string nonce,
        string origin,
        string requestId,
        string method,
        object payload,
        DateTimeOffset? deadlineUtc = null)
        => JsonSerializer.Serialize(new
        {
            protocol = AppWebBridge.Protocol,
            version = AppWebBridge.ProtocolVersion,
            nonce,
            origin,
            requestId,
            method,
            deadlineUtc = deadlineUtc?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            payload,
        });

    private static JsonElement ReadResponse(string script)
    {
        const string prefix = "window.__sunderReceive?.(";
        const string suffix = ");";
        Assert.StartsWith(prefix, script, StringComparison.Ordinal);
        Assert.EndsWith(suffix, script, StringComparison.Ordinal);
        var responseJson = JsonSerializer.Deserialize<string>(script[prefix.Length..^suffix.Length]);
        Assert.NotNull(responseJson);
        using var response = JsonDocument.Parse(responseJson);
        return response.RootElement.Clone();
    }

    private static AppWebRpcContractCatalog WriteContractCatalog(
        string root,
        byte[] descriptorBytes,
        SunderRpcContractDescriptor descriptor)
    {
        const string descriptorPath = "contracts/example.rpc.json";
        var path = Path.Combine(root, descriptorPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, descriptorBytes);
        var contentSha256 = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
        return AppWebRpcContractCatalog.Load(
            root,
            new SunderPackageManifest
            {
                Id = "caller.package",
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        Source = "owned",
                        AuthorityKind = "package",
                        AuthorityId = "caller.package",
                        ContractId = descriptor.ContractId,
                        Version = descriptor.Version,
                        DescriptorPath = descriptorPath,
                        DescriptorSha256 = descriptor.Sha256,
                        AbiSha256 = descriptor.AbiSha256,
                        Exposure = SunderPackageFormat.PublicContractExposure,
                    },
                ],
            },
            new SunderPackageContentIndex(
                SunderPackageFormat.CurrentContentIndexVersion,
                [new SunderPackageContentIndexEntry(descriptorPath, contentSha256, descriptorBytes.LongLength)]));
    }

    private static AppWebRpcContractCatalog EmptyCatalog(string root)
        => AppWebRpcContractCatalog.Load(
            root,
            new SunderPackageManifest { ContractBundles = [] },
            new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, []));

    private static RuntimeClientResourceLimits ResourceLimits()
    {
        var policy = new RuntimeResourcePolicy(
            RuntimeResourcePolicies.Mebibyte,
            16,
            RuntimeResourcePolicies.CustomMinimums.MaximumValueNodes,
            RuntimeResourcePolicies.CustomMinimums.MaximumObjectMembers,
            RuntimeResourcePolicies.CustomMinimums.MaximumInlineDecodedBytes,
            RuntimeResourcePolicies.CustomMinimums.MaximumContentReferenceBytes);
        return RuntimeClientResourceLimits.FromProfile(new RuntimeClientResourceProfile(
            Guid.NewGuid(),
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(policy),
            policy));
    }

    private static object InvocationPayload(JsonElement request)
        => new
        {
            providerHandle = "p_unknown",
            serviceId = "example",
            methodId = "ping",
            request,
        };

    private static JsonElement JsonStringWithSize(int byteCount, bool escaped)
    {
        var escapedCharacterBytes = JsonSerializer.SerializeToUtf8Bytes("\"").Length - 2;
        var payloadBytes = byteCount - 2;
        var escapedCharacters = escaped ? payloadBytes / escapedCharacterBytes : 0;
        var plainCharacters = payloadBytes - escapedCharacters * escapedCharacterBytes;
        var value = JsonSerializer.SerializeToElement(
            new string('"', escapedCharacters) + new string('x', plainCharacters));
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(value.GetRawText()));
        return value;
    }

    private static JsonElement ReplyObjectWithSize(int byteCount)
    {
        var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(new { reply = string.Empty }).Length;
        var escapedCharacterBytes = JsonSerializer.SerializeToUtf8Bytes("\"").Length - 2;
        var payloadBytes = byteCount - emptyBytes;
        var escapedCharacters = payloadBytes / escapedCharacterBytes;
        var plainCharacters = payloadBytes - escapedCharacters * escapedCharacterBytes;
        var value = JsonSerializer.SerializeToElement(new
        {
            reply = new string('"', escapedCharacters) + new string('x', plainCharacters),
        });
        Assert.Equal(byteCount, Encoding.UTF8.GetByteCount(value.GetRawText()));
        return value;
    }

    private static JsonElement NestedArray(int depth)
    {
        using var document = JsonDocument.Parse(
            new string('[', depth - 1) + "0" + new string(']', depth - 1),
            new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
    }

    private static byte[] RpcDescriptorBytes(string requestAnnotation, int responseMaxLength = 64)
        => Encoding.UTF8.GetBytes("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion": 1,
              "schemaProfile": "sunder.rpc.values.v1",
              "contractId": "example.rpc",
              "version": "1.0.0",
              "errors": {},
              "services": {
                "example": {
                  "methods": {
                    "ping": {
                      "kind": "unary",
                      "idempotency": "idempotent",
                      "errors": [],
                      "requestSchema": { "$ref": "#/$defs/Request" },
                      "responseSchema": { "$ref": "#/$defs/Response" }
                    }
                  }
                }
              },
              "$defs": {
                "Request": {
                  "description": "__REQUEST_ANNOTATION__",
                  "type": "object",
                  "properties": {
                    "message": { "type": "string", "minLength": 1, "maxLength": 32 }
                  },
                  "required": ["message"],
                  "additionalProperties": false
                },
                "Response": {
                  "type": "object",
                  "properties": {
                    "reply": { "type": "string", "minLength": 1, "maxLength": __RESPONSE_MAX_LENGTH__ }
                  },
                  "required": ["reply"],
                  "additionalProperties": false
                }
              }
            }
            """
            .Replace("__REQUEST_ANNOTATION__", requestAnnotation, StringComparison.Ordinal)
            .Replace(
                "__RESPONSE_MAX_LENGTH__",
                responseMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal));

    private static SunderPackageTargetManifest Target()
        => new()
        {
            Role = SunderPackageFormat.AppHostRole,
            Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Kind = SunderPackageFormat.WebTargetKind,
            EntryPoint = "index.html",
            RequiredHostCapabilities = ["core.v1"],
            Views =
            [
                new SunderPackageWebViewManifest
                {
                    ViewId = "example.web.main",
                    DisplayName = "Example",
                    Route = "/",
                    DefaultPlacement = "middle",
                    ShowInHotbar = true,
                },
                new SunderPackageWebViewManifest
                {
                    ViewId = "example.web.workspace",
                    DisplayName = "Workspace",
                    Route = "/workspace",
                    DefaultPlacement = "rightTop",
                    ShowInHotbar = false,
                },
            ],
        };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-web-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestWebView : IAppWebView
    {
        private readonly object _gate = new();
        private readonly List<string> _scripts = [];

        public Func<string, Task<string?>>? EvaluateScript { get; set; }
        public Action? OnDispose { get; set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Control Control { get; } = new Border();

        public IReadOnlyList<string> Scripts
        {
            get
            {
                lock (_gate) return _scripts.ToArray();
            }
        }

        public event EventHandler<AppWebNavigationStartingEventArgs>? NavigationStarting
        {
            add { }
            remove { }
        }

        public event EventHandler<AppWebNavigationCompletedEventArgs>? NavigationCompleted
        {
            add { }
            remove { }
        }

        public event EventHandler<AppWebNewWindowRequestedEventArgs>? NewWindowRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<AppWebResourceRequestedEventArgs>? ResourceRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<AppWebMessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public void Navigate(Uri uri)
        {
        }

        public Task<string?> InvokeScriptAsync(string script)
        {
            lock (_gate) _scripts.Add(script);
            return EvaluateScript?.Invoke(script) ?? Task.FromResult<string?>("true");
        }

        public void Stop()
        {
        }

        public void ClearScripts()
        {
            lock (_gate) _scripts.Clear();
        }

        public void Dispose()
        {
            var onDispose = OnDispose;
            OnDispose = null;
            onDispose?.Invoke();
            Disposed.TrySetResult();
        }
    }

    private sealed class TestWebViewFactory(IAppWebView view) : IAppWebViewFactory
    {
        public IAppWebView Create(string userDataDirectory) => view;
    }

    private sealed class BlockingRpcClient : IAppWebRpcClient
    {
        public const string SecretEndpoint = "rpc1_secret_endpoint_value";
        private int _discoverCalls;
        private int _disposeCalls;

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public CancellationToken FirstToken { get; private set; }
        public CancellationToken SecondToken { get; private set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _discoverCalls);
            if (call == 1)
            {
                FirstToken = cancellationToken;
                FirstStarted.TrySetResult();
                await FirstRelease.Task.ConfigureAwait(false);
            }
            else
            {
                SecondToken = cancellationToken;
                SecondStarted.TrySetResult();
                await SecondRelease.Task.ConfigureAwait(false);
            }
            return new SunderRpcCatalogSnapshot(
                call,
                call,
                [new SunderRpcProviderSnapshot(
                    "provider.package",
                    "1.0.0",
                    "provider.main",
                    "example.rpc",
                    "1.0.0",
                    new string('a', 64),
                    new string('b', 64),
                    Guid.NewGuid(),
                    1,
                    1,
                    new SunderRpcEndpointReference(SecretEndpoint),
                    call,
                    SunderRpcProviderState.Active)]);
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InvocationRpcClient(SunderRpcContractDescriptor providerDescriptor) : IAppWebRpcClient
    {
        public const string EndpointReference = "rpc1_annotation_different_provider";
        private int _invokeCalls;

        public int InvokeCalls => Volatile.Read(ref _invokeCalls);
        public Guid ActivationId { get; } = Guid.NewGuid();

        public string? LastEndpointReference { get; private set; }

        public SunderRpcError? Error { get; set; }

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(
                1,
                1,
                [new SunderRpcProviderSnapshot(
                    "provider.package",
                    "1.0.0",
                    "provider.main",
                    providerDescriptor.ContractId,
                    providerDescriptor.Version,
                    providerDescriptor.Sha256,
                    providerDescriptor.AbiSha256,
                    ActivationId,
                    1,
                    1,
                    new SunderRpcEndpointReference(EndpointReference),
                    1,
                    SunderRpcProviderState.Active)]));

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            CancellationToken cancellationToken)
        {
            LastEndpointReference = endpointReference;
            Interlocked.Increment(ref _invokeCalls);
            if (Error is not null)
            {
                return ValueTask.FromException<JsonElement>(new SunderRpcException(Error));
            }
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
            {
                reply = $"received {request.GetProperty("message").GetString()}",
            }));
        }

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ResourceRpcClient(
        SunderRpcContractDescriptor providerDescriptor,
        RuntimeClientResourceLimits resourceLimits) : IAppWebRpcClient
    {
        private const string EndpointReference = "rpc1_resource_provider";

        public RuntimeClientResourceLimits ResourceLimits => resourceLimits;

        public JsonElement Response { get; set; } = JsonSerializer.SerializeToElement(new { reply = "ready" });

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(
                1,
                1,
                [new SunderRpcProviderSnapshot(
                    "provider.package",
                    "1.0.0",
                    "provider.main",
                    providerDescriptor.ContractId,
                    providerDescriptor.Version,
                    providerDescriptor.Sha256,
                    providerDescriptor.AbiSha256,
                    Guid.NewGuid(),
                    1,
                    1,
                    new SunderRpcEndpointReference(EndpointReference),
                    1,
                    SunderRpcProviderState.Active)]));

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Response);

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeadlineRpcClient : IAppWebRpcClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The deadline test should be cancelled.");
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            string endpointReference,
            string serviceId,
            string methodId,
            JsonElement request,
            DateTimeOffset? deadlineUtc,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
