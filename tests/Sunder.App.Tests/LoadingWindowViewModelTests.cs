using Sunder.App.Services;
using Sunder.App.ViewModels;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LoadingWindowViewModelTests
{
    [Fact]
    public async Task FailureActions_AreAvailableOnlyAfterStartupFails()
    {
        var retries = 0;
        var coreShellOpens = 0;
        var quits = 0;
        var viewModel = new LoadingWindowViewModel();
        viewModel.ConfigureFailureActions(
            () =>
            {
                retries++;
                return Task.CompletedTask;
            },
            () =>
            {
                coreShellOpens++;
                return Task.CompletedTask;
            },
            () => quits++);

        Assert.True(viewModel.TryBeginAttempt());
        Assert.False(viewModel.RetryCommand.CanExecute(null));

        viewModel.ShowFailure(new InvalidOperationException("Runtime bootstrap failed."));

        Assert.True(viewModel.HasFailed);
        Assert.Equal("Runtime bootstrap failed.", viewModel.FailureDetails);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        Assert.True(viewModel.OpenCoreShellCommand.CanExecute(null));
        Assert.True(viewModel.QuitCommand.CanExecute(null));

        await viewModel.RetryCommand.ExecuteAsync(null);
        await viewModel.OpenCoreShellCommand.ExecuteAsync(null);
        viewModel.QuitCommand.Execute(null);

        Assert.Equal(1, retries);
        Assert.Equal(1, coreShellOpens);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void StartupAttempt_ClearsThePreviousFailure()
    {
        var viewModel = new LoadingWindowViewModel();
        viewModel.ShowFailure(new InvalidOperationException("failed"));

        Assert.True(viewModel.TryBeginAttempt());

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.HasFailed);
        Assert.Empty(viewModel.FailureDetails);
    }

    [Fact]
    public void TimeoutFailure_UsesClearDeadlineStatusAndKeepsFailureActionsAvailable()
    {
        var viewModel = new LoadingWindowViewModel();

        viewModel.ShowFailure(new TimeoutException(
            "Sunder startup did not complete within 60 seconds. Retry, open the Core Shell, or quit."));

        Assert.Equal("Sunder took too long to start.", viewModel.StatusMessage);
        Assert.Contains("60 seconds", viewModel.FailureDetails, StringComparison.Ordinal);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        Assert.True(viewModel.OpenCoreShellCommand.CanExecute(null));
        Assert.True(viewModel.QuitCommand.CanExecute(null));
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
        Assert.Contains("Retry, open the Core Shell, or quit.", timeout.Message, StringComparison.Ordinal);
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
