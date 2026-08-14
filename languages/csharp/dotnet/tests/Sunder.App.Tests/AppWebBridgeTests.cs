using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Package.Format;
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
                "error: Object.freeze({ kind, code, message, dispatchState, retrySafety })",
                bootstrap,
                StringComparison.Ordinal);
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

    private static byte[] RpcDescriptorBytes(string requestAnnotation)
        => Encoding.UTF8.GetBytes("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion": 2,
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
                    "reply": { "type": "string", "minLength": 1, "maxLength": 64 }
                  },
                  "required": ["reply"],
                  "additionalProperties": false
                }
              }
            }
            """.Replace("__REQUEST_ANNOTATION__", requestAnnotation, StringComparison.Ordinal));

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
