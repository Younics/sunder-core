using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageCallbackServerTests
{
    [Fact]
    public async Task Callback_InvokesRegisteredHandlerAndRemovesIt()
    {
        using var server = CreateServer();
        var handledQueries = new List<IReadOnlyDictionary<string, string?>>();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler(
            "test-session",
            async (queryValues, _) =>
            {
                handledQueries.Add(queryValues);
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
                return true;
            });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var firstRequest = httpClient.GetAsync(new Uri($"{server.GetCallbackUri("test-session")}?state=provider-state&code=abc"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRequest = httpClient.GetAsync(new Uri($"{server.GetCallbackUri("test-session")}?state=provider-state&code=abc"));
        releaseHandler.TrySetResult();
        var httpResponses = await Task.WhenAll(firstRequest, secondRequest);
        var responses = await Task.WhenAll(httpResponses.Select(response => response.Content.ReadAsStringAsync()));

        Assert.Single(responses, response => response.Contains("Authorization received", StringComparison.Ordinal));
        Assert.Single(responses, response => response.Contains("Authorization failed", StringComparison.Ordinal));
        Assert.Contains(httpResponses, response => response.StatusCode == System.Net.HttpStatusCode.Accepted);
        Assert.Contains(httpResponses, response => response.StatusCode == System.Net.HttpStatusCode.NotFound);
        var queryValues = Assert.Single(handledQueries);
        Assert.Equal("provider-state", queryValues["state"]);
        Assert.Equal("abc", queryValues["code"]);
    }

    [Fact]
    public async Task AuthenticationCallback_UsesFixedPathAndDispatchesByState()
    {
        using var server = CreateServer();
        IReadOnlyDictionary<string, string?>? handledQuery = null;
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("auth-session", (queryValues, _) =>
        {
            handledQuery = queryValues;
            handlerCompleted.TrySetResult();
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var callbackUri = server.GetAuthenticationCallbackUri();
        var response = await httpClient.GetStringAsync(
            new Uri($"{callbackUri}?state=auth-session&code=abc"));

        Assert.Equal("/auth/callback", callbackUri.AbsolutePath);
        Assert.Contains("Authorization received", response, StringComparison.Ordinal);
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(handledQuery);
        Assert.Equal("auth-session", handledQuery["state"]);
        Assert.Equal("abc", handledQuery["code"]);
        Assert.Equal(0, server.RegisteredHandlerCount);
    }

    [Fact]
    public async Task AuthenticationCallback_MissingStateDoesNotConsumeHandler()
    {
        using var server = CreateServer();
        var invocationCount = 0;
        server.RegisterHandler("auth-session", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var httpResponse = await httpClient.GetAsync(server.GetAuthenticationCallbackUri());
        var response = await httpResponse.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, httpResponse.StatusCode);
        Assert.Contains("Invalid callback path", response, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        Assert.Equal(1, server.RegisteredHandlerCount);
    }

    [Fact]
    public async Task Callback_ReturnsFailureForUnknownState()
    {
        using var server = CreateServer();
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var httpResponse = await httpClient.GetAsync(server.GetCallbackUri("missing"));
        var response = await httpResponse.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, httpResponse.StatusCode);
        Assert.Contains("Authorization failed", response, StringComparison.Ordinal);
        Assert.Contains("No matching callback session", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_StopsIntakeWithoutCancellingActiveCallbacks()
    {
        await using var server = CreateServer();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;
        server.RegisterHandler("test-session", async (_, cancellationToken) =>
        {
            handlerEntered.TrySetResult();
            try
            {
                await releaseHandler.Task;
                return true;
            }
            finally
            {
                cancellationObserved = cancellationToken.IsCancellationRequested;
                handlerExited.TrySetResult();
            }
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var request = httpClient.GetAsync(server.GetCallbackUri("test-session"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = server.StopAsync(CancellationToken.None);

        Assert.False(stop.IsCompleted);
        Assert.False(cancellationObserved);
        releaseHandler.TrySetResult();
        await stop;
        await handlerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.RegisteredHandlerCount);
        using var response = await request;
        Assert.False(cancellationObserved);
    }

    [Fact]
    public async Task MalformedCallback_DoesNotConsumePendingHandler()
    {
        var policy = new RuntimeAuthPolicyOptions
        {
            PackageCallbackPort = GetFreePort(),
            PackageCallbackFallbackPort = GetFreePort(),
            MaxPackageCallbackQueryValueLength = 3,
        };
        await using var server = new PackageCallbackServer(NullLogger<PackageCallbackServer>.Instance, policy);
        var invocationCount = 0;
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("test-session", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            handlerCompleted.TrySetResult();
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var malformedResponse = await httpClient.GetAsync(
            new Uri($"{server.GetCallbackUri("test-session")}?code=oversized"));
        var malformed = await malformedResponse.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Contains("oversized value", malformed, StringComparison.Ordinal);
        Assert.Equal(1, server.RegisteredHandlerCount);
        Assert.Equal(0, invocationCount);

        var valid = await httpClient.GetStringAsync(
            new Uri($"{server.GetCallbackUri("test-session")}?code=ok"));

        Assert.Contains("Authorization received", valid, StringComparison.Ordinal);
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.RegisteredHandlerCount);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task Callback_AcknowledgesBeforeHandlerCompletes()
    {
        await using var server = CreateServer();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("test-session", async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
            return true;
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await httpClient.GetAsync(server.GetCallbackUri("test-session"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("Authorization received", body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseHandler.TrySetResult();
    }

    [Fact]
    public async Task Callback_IsReachableThroughIpv4Loopback()
    {
        await using var server = CreateServer();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("test-session", (_, _) =>
        {
            handled.TrySetResult();
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        var callback = server.GetCallbackUri("test-session");

        using var ipv4Client = CreateLoopbackClient(System.Net.Sockets.AddressFamily.InterNetwork);
        var response = await ipv4Client.GetStringAsync(callback);

        Assert.Contains("Authorization received", response, StringComparison.Ordinal);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("localhost", callback.Host);
    }

    [Fact]
    public async Task Callback_IsReachableThroughIpv6Loopback()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        await using var server = CreateServer();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("test-session", (_, _) =>
        {
            handled.TrySetResult();
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        var callback = server.GetCallbackUri("test-session");

        using var ipv6Client = CreateLoopbackClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
        var response = await ipv6Client.GetStringAsync(callback);

        Assert.Contains("Authorization received", response, StringComparison.Ordinal);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuthenticationCallback_StateComparisonIsOrdinal()
    {
        await using var server = CreateServer();
        var invocationCount = 0;
        server.RegisterHandler("auth-session", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var httpResponse = await httpClient.GetAsync(
            new Uri($"{server.GetAuthenticationCallbackUri()}?state=AUTH-SESSION&code=abc"));
        var response = await httpResponse.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, httpResponse.StatusCode);
        Assert.Contains("No matching callback session", response, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        Assert.Equal(1, server.RegisteredHandlerCount);
    }

    [Fact]
    public async Task Callback_DuplicateQueryValueDoesNotConsumeHandler()
    {
        await using var server = CreateServer();
        var invocationCount = 0;
        server.RegisterHandler("test-session", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var httpResponse = await httpClient.GetAsync(
            new Uri($"{server.GetCallbackUri("test-session")}?code=one&code=two"));
        var response = await httpResponse.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, httpResponse.StatusCode);
        Assert.Contains("duplicate value", response, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        Assert.Equal(1, server.RegisteredHandlerCount);
    }

    [Fact]
    public async Task AuthenticationCallback_RoutesDerivedStateForPackageValidation()
    {
        await using var server = CreateServer();
        IReadOnlyDictionary<string, string?>? handledQuery = null;
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("auth-session", (query, _) =>
        {
            handledQuery = query;
            handlerCompleted.TrySetResult();
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        const string derivedState = "auth-session.onboarding_entrypoint=life_sciences";

        using var response = await httpClient.GetAsync(
            new Uri($"{server.GetAuthenticationCallbackUri()}?state={derivedState}&code=abc"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(derivedState, handledQuery?["state"]);
        Assert.Equal(0, server.RegisteredHandlerCount);
    }

    [Fact]
    public void EnsureStarted_UsesFallbackPortWhenPrimaryPortIsUnavailable()
    {
        var primaryPort = GetFreePort();
        using var occupied = new TcpListener(System.Net.IPAddress.Loopback, primaryPort);
        occupied.Start();
        var fallbackPort = GetFreePort();
        var policy = new RuntimeAuthPolicyOptions
        {
            PackageCallbackPort = primaryPort,
            PackageCallbackFallbackPort = fallbackPort,
        };
        using var server = new PackageCallbackServer(NullLogger<PackageCallbackServer>.Instance, policy);

        server.EnsureStarted();

        Assert.Equal(fallbackPort, server.BaseUri.Port);
        Assert.Equal("localhost", server.BaseUri.Host);
    }

    [Fact]
    public void DefaultQueryBounds_MatchWorkerProtocolBounds()
    {
        var policy = new RuntimeAuthPolicyOptions();

        Assert.Equal(PackageCallbackParameters.MaximumCount, policy.MaxPackageCallbackQueryValues);
        Assert.Equal(PackageCallbackParameters.MaximumKeyLength, policy.MaxPackageCallbackQueryKeyLength);
        Assert.Equal(PackageCallbackParameters.MaximumValueLength, policy.MaxPackageCallbackQueryValueLength);
        Assert.Equal(PackageCallbackParameters.MaximumTotalLength, policy.MaxPackageCallbackQueryCharacters);
    }

    private static PackageCallbackServer CreateServer()
        => new(NullLogger<PackageCallbackServer>.Instance, GetFreePort());

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpClient CreateLoopbackClient(System.Net.Sockets.AddressFamily addressFamily)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(
                    addressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        addressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            ? System.Net.IPAddress.Loopback
                            : System.Net.IPAddress.IPv6Loopback,
                        context.DnsEndPoint.Port,
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
}
