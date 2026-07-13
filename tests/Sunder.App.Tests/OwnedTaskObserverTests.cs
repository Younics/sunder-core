using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class OwnedTaskObserverTests
{
    [Fact]
    public async Task StopAsync_CancelsAndDrainsOwnedTasksAndIsIdempotent()
    {
        using var observer = new OwnedTaskObserver("test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.Run(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                finished.SetResult();
            }
        }, "waiting");
        await started.Task;

        var first = observer.StopAsync();
        var second = observer.StopAsync();

        Assert.Same(first, second);
        await first;
        Assert.True(finished.Task.IsCompletedSuccessfully);
        Assert.True(observer.Token.IsCancellationRequested);
    }
}
