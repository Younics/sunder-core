using System.Runtime.CompilerServices;
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
                AppWebRpcContractCatalog.Load(root, new SunderPackageManifest { ContractBundles = [] }),
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

    private static string Request(string nonce, string origin, string requestId, string method, object payload)
        => JsonSerializer.Serialize(new
        {
            protocol = AppWebBridge.Protocol,
            version = AppWebBridge.ProtocolVersion,
            nonce,
            origin,
            requestId,
            method,
            payload,
        });

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
}
