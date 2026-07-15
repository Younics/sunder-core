using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LatestAsyncRequestTests
{
    [Fact]
    public async Task Start_CancellationCallbackCanReenterOwnerWithoutDeadlock()
    {
        using var request = new LatestAsyncRequest();
        using var first = request.Start();
        using var registration = first.Token.Register(() => request.IsCurrent(first.Generation));

        var replacement = await Task.Run(() => request.Start()).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(replacement.IsCurrent);
        replacement.Dispose();
    }
}
