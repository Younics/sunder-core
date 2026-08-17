using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebBridgeTests
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
            await bridge.EstablishAsync(server.BaseUri);
            var bootstrap = Assert.Single(webView.Scripts);
            Assert.Contains(
                "if (details !== undefined) error.details = freezeJson(details);",
                bootstrap,
                StringComparison.Ordinal);
            Assert.Contains("detail.kind !== 'domain'", bootstrap, StringComparison.Ordinal);
            Assert.Contains("const encoder = new TextEncoder();", bootstrap, StringComparison.Ordinal);
            Assert.Contains("rpcValueBytes: 8388608", bootstrap, StringComparison.Ordinal);
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
            Assert.False(discoveredProvider.TryGetProperty("endpointReference", out _));
            Assert.False(discoveredProvider.TryGetProperty("activationId", out _));
            var providerHandle = discoveredProvider.GetProperty("providerHandle").GetString();
            Assert.NotNull(providerHandle);

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

            bridge.NavigationStarting();
            var firstNonce = bridge.SessionNonce;
            await bridge.HandleMessageAsync(Request(firstNonce, origin, "b_0", "rpc.discover", new { contractId = "example.rpc" }));
            Assert.Equal(0, rpc.DiscoverCalls);

            await bridge.EstablishAsync(server.BaseUri);
            webView.ClearScripts();
            var firstRequest = bridge.HandleMessageAsync(
                Request(firstNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await rpc.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            bridge.NavigationStarting();
            var secondNonce = bridge.SessionNonce;
            Assert.NotEqual(firstNonce, secondNonce);
            await bridge.EstablishAsync(server.GetRouteUri("/workspace"));
            webView.ClearScripts();
            var secondRequest = bridge.HandleMessageAsync(
                Request(secondNonce, origin, "b_1", "rpc.discover", new { contractId = "example.rpc" }));
            await rpc.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            rpc.FirstRelease.TrySetResult();
            await firstRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, bridge.PendingRequestCount);
            Assert.Empty(webView.Scripts);

            rpc.SecondRelease.TrySetResult();
            await secondRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, bridge.PendingRequestCount);
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
                {"requestId":"b_malformed","protocol":"{{AppWebBridge.Protocol}}","broken":
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
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
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
              "descriptorVersion": 2,
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
            return Task.FromResult<string?>(null);
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
        }
    }

    private sealed class BlockingRpcClient : IAppWebRpcClient
    {
        public const string SecretEndpoint = "rpc1_secret_endpoint_value";
        private int _discoverCalls;

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);
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
                FirstStarted.TrySetResult();
                await FirstRelease.Task.ConfigureAwait(false);
            }
            else
            {
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvocationRpcClient(SunderRpcContractDescriptor providerDescriptor) : IAppWebRpcClient
    {
        public const string EndpointReference = "rpc1_annotation_different_provider";
        private int _invokeCalls;

        public int InvokeCalls => Volatile.Read(ref _invokeCalls);

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
