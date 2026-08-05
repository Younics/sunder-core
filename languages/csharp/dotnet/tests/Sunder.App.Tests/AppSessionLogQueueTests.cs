using Sunder.App.Services;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppSessionLogQueueTests
{
    [Fact]
    public async Task FlushAsync_WhenDataWasDropped_QueuesDropReportAndNonDroppableCompletion()
    {
        var queue = new AppSessionLogQueue(capacity: 1);
        Assert.True(queue.TryWrite(AppSessionLogEntry.Write(PackageLogLevel.Information, "accepted", null)));
        Assert.False(queue.TryWrite(AppSessionLogEntry.Write(PackageLogLevel.Information, "dropped", null)));

        var flush = queue.FlushAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("accepted", reader.Current.Message);
        Assert.True(await reader.MoveNextAsync());
        Assert.Contains("Dropped 1 file log entry", reader.Current.Message, StringComparison.Ordinal);
        Assert.True(await reader.MoveNextAsync());
        Assert.NotNull(reader.Current.FlushCompletion);
        reader.Current.FlushCompletion!.TrySetResult();

        await flush;
        Assert.Equal(0, queue.DroppedEntries);
    }

    [Fact]
    public async Task FlushAsync_WhenConsumerCannotAdvance_TimesOut()
    {
        var queue = new AppSessionLogQueue(capacity: 1);
        Assert.True(queue.TryWrite(AppSessionLogEntry.Write(PackageLogLevel.Information, "blocks flush", null)));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            queue.FlushAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}
