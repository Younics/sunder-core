using Sunder.App.Services;
using Sunder.Runtime.Client;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeConnectionStateTests
{
    [Fact]
    public void GetConnectionInfo_RefreshesRotatedSupervisorCredential()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:5275/");
        var published = new RuntimeConnectionInfo(runtimeUrl, "first-token");
        var state = new RuntimeConnectionState(runtimeUrl, _ => published);

        Assert.Equal("first-token", state.GetConnectionInfo()?.BearerToken);

        published = new RuntimeConnectionInfo(runtimeUrl, "rotated-token");

        Assert.Equal("rotated-token", state.GetConnectionInfo()?.BearerToken);
        Assert.Equal("rotated-token", state.ConnectionInfo?.BearerToken);
    }

    [Fact]
    public void GetConnectionInfo_KeepsLastCredentialDuringAtomicReplacementGap()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:5275/");
        RuntimeConnectionInfo? published = new(runtimeUrl, "current-token");
        var state = new RuntimeConnectionState(runtimeUrl, _ => published);

        published = null;

        Assert.Equal("current-token", state.GetConnectionInfo()?.BearerToken);
    }

    [Fact]
    public async Task GetConnectionInfo_DoesNotReturnPreviousUrlCredentialAfterConcurrentChange()
    {
        var firstUrl = new Uri("http://127.0.0.1:5275/");
        var secondUrl = new Uri("http://127.0.0.1:5276/");
        using var refreshStarted = new ManualResetEventSlim();
        using var releaseRefresh = new ManualResetEventSlim();
        var firstLoad = true;
        var state = new RuntimeConnectionState(firstUrl, url =>
        {
            if (url == secondUrl)
            {
                return new RuntimeConnectionInfo(secondUrl, "second-token");
            }
            if (firstLoad)
            {
                firstLoad = false;
                return new RuntimeConnectionInfo(firstUrl, "first-token");
            }
            refreshStarted.Set();
            releaseRefresh.Wait(TimeSpan.FromSeconds(5));
            return null;
        });
        var refresh = Task.Run(state.GetConnectionInfo);
        Assert.True(refreshStarted.Wait(TimeSpan.FromSeconds(5)));

        state.RuntimeUrl = secondUrl;
        releaseRefresh.Set();

        var connection = await refresh;
        Assert.NotNull(connection);
        Assert.Equal("second-token", connection.BearerToken);
    }
}
