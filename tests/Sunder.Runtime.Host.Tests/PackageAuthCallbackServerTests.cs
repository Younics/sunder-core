using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageAuthCallbackServerTests
{
    [Fact]
    public async Task Callback_InvokesRegisteredHandlerAndRemovesIt()
    {
        using var server = CreateServer();
        var handledQueries = new List<IReadOnlyDictionary<string, string?>>();
        server.RegisterHandler(
            "test-session",
            (queryValues, _) =>
            {
                handledQueries.Add(queryValues);
                return Task.FromResult(true);
            });
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var firstResponse = await httpClient.GetStringAsync(new Uri($"{server.CallbackUri}?state=test-session&code=abc"));
        var secondResponse = await httpClient.GetStringAsync(new Uri($"{server.CallbackUri}?state=test-session&code=abc"));

        Assert.Contains("Authorization complete", firstResponse, StringComparison.Ordinal);
        Assert.Contains("Authorization failed", secondResponse, StringComparison.Ordinal);
        var queryValues = Assert.Single(handledQueries);
        Assert.Equal("test-session", queryValues["state"]);
        Assert.Equal("abc", queryValues["code"]);
    }

    [Fact]
    public async Task Callback_ReturnsFailureForUnknownState()
    {
        using var server = CreateServer();
        server.EnsureStarted();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await httpClient.GetStringAsync(new Uri($"{server.CallbackUri}?state=missing"));

        Assert.Contains("Authorization failed", response, StringComparison.Ordinal);
        Assert.Contains("No matching authorization session", response, StringComparison.Ordinal);
    }

    private static PackageAuthCallbackServer CreateServer()
        => new(NullLogger<PackageAuthCallbackServer>.Instance, GetFreePort());

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
