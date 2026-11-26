using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Integration.Tests;

public sealed class BrowserProbeDeadlineTests
{
    [Fact]
    public async Task LateControllerAndImmediateControllerShareOneDeadlineWithLaterDocument()
    {
        var clock = new ManualTimeProvider();
        var deadline = new BrowserProbeDeadline(TimeSpan.FromMinutes(1), clock);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = BrowserProbeDeadline.BothAsync(deadline.ObserveAsync("a/controller", late.Task),
            deadline.ObserveAsync("b/controller", Task.CompletedTask));
        clock.Advance(TimeSpan.FromSeconds(27));
        Assert.False(pair.IsCompleted);
        late.SetResult();
        await pair;
        var document = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = deadline.ObserveAsync("a/document", document.Task);
        clock.Advance(TimeSpan.FromSeconds(33));
        Assert.Contains("a/document", (await Assert.ThrowsAsync<TimeoutException>(() => observation)).Message);
        Assert.False(document.Task.IsCompleted);
        document.SetResult(); // Expiring an observation never releases the owner's operation.
    }

    [Fact]
    public async Task FaultedControllerIsReportedWhileTheOtherViewIsStillPending()
    {
        var deadline = new BrowserProbeDeadline(TimeSpan.FromMinutes(1), new ManualTimeProvider());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TimeoutException("native controller failure");
        var pair = BrowserProbeDeadline.BothAsync(pending.Task, deadline.ObserveAsync("b/controller", Task.FromException(native)));
        Assert.Same(native, await Assert.ThrowsAsync<TimeoutException>(() => pair));
        Assert.False(pending.Task.IsCompleted);
        pending.SetResult();
    }

    [Fact]
    public async Task NavigationFailureInterruptsPendingDocumentReadiness()
    {
        var clock = new ManualTimeProvider();
        var deadline = new BrowserProbeDeadline(TimeSpan.FromMinutes(1), clock);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new InvalidDataException("observed native failure");
        var observed = deadline.ObserveAsync("a/workers", pending.Task, failure.Task);
        failure.SetResult(native);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => observed);
        Assert.Contains("a/workers", error.Message);
        Assert.Same(native, error.InnerException);
        Assert.False(pending.Task.IsCompleted);
        pending.SetResult();
    }
}
