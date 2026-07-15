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

    [Fact]
    public async Task StopAsync_CancellationCallbackCanReenterObserverWithoutDeadlock()
    {
        using var observer = new OwnedTaskObserver("test");
        using var registration = observer.Token.Register(() =>
            observer.Observe(Task.CompletedTask, "handling cancellation"));
        observer.Run(token => Task.Delay(Timeout.InfiniteTimeSpan, token), "waiting");

        await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(observer.Token.IsCancellationRequested);
    }
}
