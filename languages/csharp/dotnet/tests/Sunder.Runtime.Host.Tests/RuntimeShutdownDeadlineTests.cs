using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeShutdownDeadlineTests
{
    [Fact]
    public async Task DeadlineStartsAtApplicationStoppingAndBoundsAllCleanup()
    {
        using var applicationStopping = new CancellationTokenSource();
        using var deadline = new RuntimeShutdownDeadline(
            applicationStopping.Token,
            TimeSpan.FromMilliseconds(50));

        Assert.False(deadline.Token.IsCancellationRequested);
        applicationStopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token).WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
