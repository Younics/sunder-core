using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Runtime.Client;
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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void StartupAttemptDeadline_NormalAndCoreShellStartupRetainDefaultBudgets(
        bool openCoreShell,
        int devPackageCount)
    {
        using var transport = CreateRuntimeTransport(TimeSpan.FromMinutes(10));
        using var deadline = StartupAttemptDeadline.CreateForStartup(
            CancellationToken.None,
            openCoreShell,
            devPackageCount,
            transport);

        Assert.Equal(TimeSpan.FromSeconds(60), deadline.TotalBudget);
        Assert.Equal(
            TimeSpan.FromSeconds(45),
            deadline.GetPhaseBudget(StartupPhase.RuntimePackages));
    }

    [Theory]
    [InlineData(600, 900, 720)]
    [InlineData(100, 150, 120)]
    public void StartupAttemptDeadline_DevPackageStartupDerivesBudgetsFromTransportPolicy(
        int requestTimeoutSeconds,
        int expectedTotalSeconds,
        int expectedRuntimePackagesSeconds)
    {
        using var transport = CreateRuntimeTransport(
            TimeSpan.FromSeconds(requestTimeoutSeconds));
        using var deadline = StartupAttemptDeadline.CreateForStartup(
            CancellationToken.None,
            openCoreShell: false,
            devPackageCount: 2,
            transport);

        Assert.Equal(TimeSpan.FromSeconds(expectedTotalSeconds), deadline.TotalBudget);
        Assert.Equal(
            TimeSpan.FromSeconds(expectedRuntimePackagesSeconds),
            deadline.GetPhaseBudget(StartupPhase.RuntimePackages));
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

    private static RuntimeClientTransport CreateRuntimeTransport(TimeSpan requestTimeout) =>
        new(
            static () => null,
            policy: new RuntimeClientPolicyOptions { RequestTimeout = requestTimeout });
}
