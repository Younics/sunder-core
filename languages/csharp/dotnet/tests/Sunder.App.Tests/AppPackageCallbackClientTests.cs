using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageCallbackClientTests
{
    private const string AuthorizationUrl =
        "https://auth.openai.com/oauth/authorize?response_type=code"
        + "&client_id=app_EMoamEEZ73f0CkXaXp7hrann"
        + "&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback"
        + "&scope=openid%20profile%20email%20offline_access"
        + "&code_challenge=test-challenge"
        + "&code_challenge_method=S256"
        + "&state=test-session";

    [Fact]
    public async Task CallbackLaunch_PreservesEncodedAuthorizationUrlAcrossRuntimeBoundary()
    {
        var handler = new CallbackResponseHandler(AuthorizationUrl);
        using var transport = new RuntimePackageCallbackClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-token"),
            handler);
        ProcessStartInfo? capturedStartInfo = null;
        var browser = new ExternalBrowserService(startInfo => capturedStartInfo = startInfo);
        var callbacks = new AppPackageCallbackClient("test.package", transport, browser);

        var status = await callbacks.StartAsync("authentication");
        Assert.NotNull(status.LaunchUri);
        await callbacks.OpenLaunchUriAsync(status.LaunchUri);

        Assert.Equal(AuthorizationUrl, status.LaunchUri.OriginalString);
        Assert.NotNull(capturedStartInfo);
        Assert.Equal(AuthorizationUrl, capturedStartInfo.FileName);
        Assert.DoesNotContain("%253A", capturedStartInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%252F", capturedStartInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(capturedStartInfo.UseShellExecute);
    }

    [Fact]
    public void ExternalBrowserService_UsesExactUriConstructorInput()
    {
        ProcessStartInfo? capturedStartInfo = null;
        var browser = new ExternalBrowserService(startInfo => capturedStartInfo = startInfo);

        browser.Open(new Uri(AuthorizationUrl, UriKind.Absolute));

        Assert.NotNull(capturedStartInfo);
        Assert.Equal(AuthorizationUrl, capturedStartInfo.FileName);
    }

    [Fact]
    public async Task CancelAsync_RequestsRuntimeCancellation()
    {
        var handler = new CallbackLifecycleHandler();
        using var transport = new RuntimePackageCallbackClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-token"),
            handler);
        var callbacks = new AppPackageCallbackClient(
            "test.package",
            transport,
            new ExternalBrowserService(_ => { }));

        Assert.True(await callbacks.CancelAsync("test-session"));
        Assert.Equal(HttpMethod.Delete, handler.LastSessionMethod);
    }

    [Fact]
    public async Task WaitForCompletionAsync_PollsUntilTerminal()
    {
        var handler = new CallbackLifecycleHandler(completeAfterStatusRequests: 2);
        using var transport = new RuntimePackageCallbackClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-token"),
            handler);
        var callbacks = new AppPackageCallbackClient(
            "test.package",
            transport,
            new ExternalBrowserService(_ => { }));

        var status = await callbacks.WaitForCompletionAsync("test-session", TimeSpan.FromSeconds(2));

        Assert.Equal(Sunder.Sdk.Callbacks.PackageCallbackSessionState.Completed, status.State);
        Assert.True(status.IsTerminal);
        Assert.Equal(2, handler.StatusRequestCount);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTimeoutExpires_ThrowsTimeoutException()
    {
        var handler = new CallbackLifecycleHandler();
        using var transport = new RuntimePackageCallbackClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-token"),
            handler);
        var callbacks = new AppPackageCallbackClient(
            "test.package",
            transport,
            new ExternalBrowserService(_ => { }));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            callbacks.WaitForCompletionAsync("test-session", TimeSpan.FromSeconds(1)).AsTask());
        Assert.True(handler.StatusRequestCount > 0);
    }

    private sealed class CallbackResponseHandler(string launchUri) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RuntimeHandshakeResponse(
                        RuntimeProtocol.Identity,
                        RuntimeProtocol.CurrentRevision,
                        RuntimeProtocol.MinimumSupportedRevision,
                        RuntimeProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1],
                        new RuntimeProductVersionDiagnostics(
                            "Sunder.Runtime.Host",
                            "Development",
                            "Development"))),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PackageCallbackSessionResponse(
                    "test.package",
                    "authentication",
                    "test-session",
                    PackageCallbackSessionState.Pending,
                    "Continue in the browser.",
                    launchUri,
                    DateTimeOffset.UtcNow.AddMinutes(5))),
            });
        }
    }

    private sealed class CallbackLifecycleHandler(int completeAfterStatusRequests = int.MaxValue) : HttpMessageHandler
    {
        private int _statusRequestCount;

        public int StatusRequestCount => Volatile.Read(ref _statusRequestCount);
        public HttpMethod? LastSessionMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RuntimeHandshakeResponse(
                        RuntimeProtocol.Identity,
                        RuntimeProtocol.CurrentRevision,
                        RuntimeProtocol.MinimumSupportedRevision,
                        RuntimeProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1],
                        new RuntimeProductVersionDiagnostics(
                            "Sunder.Runtime.Host",
                            "Development",
                            "Development"))),
                });
            }

            LastSessionMethod = request.Method;
            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(true),
                });
            }

            var requestCount = Interlocked.Increment(ref _statusRequestCount);
            var state = requestCount >= completeAfterStatusRequests
                ? PackageCallbackSessionState.Completed
                : PackageCallbackSessionState.Pending;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PackageCallbackSessionResponse(
                    "test.package",
                    "authentication",
                    "test-session",
                    state,
                    state == PackageCallbackSessionState.Completed ? "Completed." : "Pending.",
                    LaunchUri: null,
                    DateTimeOffset.UtcNow.AddMinutes(5))),
            });
        }
    }
}
