using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Host.Services;
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

        var firstRequest = httpClient.GetStringAsync(new Uri($"{server.GetCallbackUri("test-session")}?state=provider-state&code=abc"));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRequest = httpClient.GetStringAsync(new Uri($"{server.GetCallbackUri("test-session")}?state=provider-state&code=abc"));
        releaseHandler.TrySetResult();
        var responses = await Task.WhenAll(firstRequest, secondRequest);

        Assert.Single(responses, response => response.Contains("Authorization complete", StringComparison.Ordinal));
        Assert.Single(responses, response => response.Contains("Authorization failed", StringComparison.Ordinal));
        var queryValues = Assert.Single(handledQueries);
        Assert.Equal("provider-state", queryValues["state"]);
        Assert.Equal("abc", queryValues["code"]);
    }

    [Fact]
    public async Task AuthenticationCallback_UsesFixedPathAndDispatchesByState()
    {
        using var server = CreateServer();
        IReadOnlyDictionary<string, string?>? handledQuery = null;
        server.RegisterHandler("auth-session", (queryValues, _) =>
        {
            handledQuery = queryValues;
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var callbackUri = server.GetAuthenticationCallbackUri();
        var response = await httpClient.GetStringAsync(
            new Uri($"{callbackUri}?state=auth-session&code=abc"));

        Assert.Equal("/auth/callback", callbackUri.AbsolutePath);
        Assert.Contains("Authorization complete", response, StringComparison.Ordinal);
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

        var response = await httpClient.GetStringAsync(server.GetAuthenticationCallbackUri());

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

        var response = await httpClient.GetStringAsync(server.GetCallbackUri("missing"));

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
        server.RegisterHandler("test-session", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult(true);
        });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var malformed = await httpClient.GetStringAsync(
            new Uri($"{server.GetCallbackUri("test-session")}?code=oversized"));

        Assert.Contains("oversized value", malformed, StringComparison.Ordinal);
        Assert.Equal(1, server.RegisteredHandlerCount);
        Assert.Equal(0, invocationCount);

        var valid = await httpClient.GetStringAsync(
            new Uri($"{server.GetCallbackUri("test-session")}?code=ok"));

        Assert.Contains("Authorization complete", valid, StringComparison.Ordinal);
        Assert.Equal(0, server.RegisteredHandlerCount);
        Assert.Equal(1, invocationCount);
    }

    private static PackageCallbackServer CreateServer()
        => new(NullLogger<PackageCallbackServer>.Instance, GetFreePort());

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
