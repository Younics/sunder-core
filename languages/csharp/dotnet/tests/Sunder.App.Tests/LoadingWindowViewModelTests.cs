using Sunder.App.Services;
using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LoadingWindowViewModelTests
{
    [Fact]
    public void StartupAttempt_PreventsOverlapAndResetsProgressWhenRestarted()
    {
        var viewModel = new LoadingWindowViewModel();

        Assert.True(viewModel.TryBeginAttempt());
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.TryBeginAttempt());

        viewModel.StatusMessage = "Starting runtime...";
        viewModel.ProgressWidth = 248;
        viewModel.CompleteAttempt();

        Assert.True(viewModel.TryBeginAttempt());
        Assert.Equal("Loading shell...", viewModel.StatusMessage);
        Assert.Equal(56, viewModel.ProgressWidth);
    }

    [Fact]
    public void CoreShellFallback_UpdatesLoadingStatusWithoutExposingFailureState()
    {
        var viewModel = new LoadingWindowViewModel();
        Assert.True(viewModel.TryBeginAttempt());

        viewModel.BeginCoreShellFallback();

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Opening Core Shell...", viewModel.StatusMessage);
        Assert.Equal(56, viewModel.ProgressWidth);
    }

    [Fact]
    public async Task StartupAttemptDeadline_CancelsLinkedTokenAndCreatesClearTimeout()
    {
        using var deadline = new StartupAttemptDeadline(
            CancellationToken.None,
            TimeSpan.FromMilliseconds(20));

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
        var timeout = deadline.CreateTimeoutException(cancellation);

        Assert.True(deadline.HasExpired);
        Assert.Contains("Sunder startup did not complete within 20 milliseconds", timeout.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupAttemptDeadline_ReportsTheExpiredPhase()
    {
        using var deadline = new StartupAttemptDeadline(
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
        deadline.EnterPhase(StartupPhase.RuntimePackages, TimeSpan.FromMilliseconds(20));

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
        var timeout = deadline.CreateTimeoutException(cancellation);

        Assert.Equal(StartupPhase.RuntimePackages, deadline.CurrentPhase);
        Assert.Equal(StartupPhase.RuntimePackages, deadline.ExpiredPhase);
        Assert.Contains("loading Runtime packages", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("20 milliseconds", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupAttemptDeadline_EnteringNextPhaseReplacesThePreviousPhaseBudget()
    {
        using var deadline = new StartupAttemptDeadline(
            CancellationToken.None,
            TimeSpan.FromSeconds(2));
        deadline.EnterPhase(StartupPhase.Theme, TimeSpan.FromMilliseconds(100));
        deadline.EnterPhase(StartupPhase.RuntimeHost, TimeSpan.FromMilliseconds(500));
        await Task.Delay(200);

        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.Equal(StartupPhase.RuntimeHost, deadline.CurrentPhase);
    }

    [Fact]
    public async Task StartupAttemptDeadline_CommitAtomicallyDisarmsTheRevealDeadline()
    {
        using var deadline = new StartupAttemptDeadline(
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
        deadline.EnterPhase(StartupPhase.Reveal, TimeSpan.FromMilliseconds(20));

        Assert.True(deadline.TryCommit());
        await Task.Delay(50);

        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.HasExpired);
    }

    [Fact]
    public async Task StartupAttemptDeadline_CommitRejectsAnElapsedDeadline()
    {
        using var deadline = new StartupAttemptDeadline(
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
        deadline.EnterPhase(StartupPhase.Reveal, TimeSpan.FromMilliseconds(20));
        await Task.Delay(50);

        Assert.False(deadline.TryCommit());
        Assert.True(deadline.HasExpired);
        Assert.Equal(StartupPhase.Reveal, deadline.ExpiredPhase);
    }

    [Fact]
    public void StartupAttemptDeadline_LinksApplicationCancellationWithoutReportingTimeout()
    {
        using var applicationCancellation = new CancellationTokenSource();
        using var deadline = new StartupAttemptDeadline(
            applicationCancellation.Token,
            TimeSpan.FromMinutes(1));

        applicationCancellation.Cancel();

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.HasExpired);
    }
}
