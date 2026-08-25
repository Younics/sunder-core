using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageUiRecoveryCoordinatorTests
{
    [Fact]
    public async Task Recovery_CoalescesNormalizedDuplicateAndWaitsForProvenCleanup()
    {
        await using var host = CreateHost();
        var notifications = new CapturingNotificationService();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var cleanup = new TaskCompletionSource<AppPackageUiCleanupOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationCount = 0;
        using var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, _, _) =>
            {
                Interlocked.Increment(ref presentationCount);
                presentationStarted.TrySetResult();
                return Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
            },
            tasks);
        var request = new AppPackageUiRecoveryRequest(
            " Agent ",
            " ABC123 ",
            Guid.NewGuid(),
            cleanup.Task,
            CreateFaultNotice(notifications, "agent", "abc123"));

        Assert.True(coordinator.TrySchedule(request));
        Assert.False(coordinator.TrySchedule(request with
        {
            PackageId = "agent",
            ContentHash = "abc123",
        }));
        await Eventually.AssertPendingAsync(
            presentationStarted.Task,
            () => $"cleanupCompleted={cleanup.Task.IsCompleted}; presentationCount={Volatile.Read(ref presentationCount)}");
        Assert.Equal(0, Volatile.Read(ref presentationCount));

        cleanup.SetResult(AppPackageUiCleanupOutcome.Proven);
        await presentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.WaitForRecoveryAsync("agent", "abc123")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref presentationCount));
        Assert.Equal(1, coordinator.AttemptedRecoveryCount);
        Assert.Equal(0, notifications.PublishCount);
    }

    [Fact]
    public async Task Recovery_UnprovenCleanupNeverOverlapsActivationAndRemainsTerminal()
    {
        await using var host = CreateHost();
        var notifications = new CapturingNotificationService();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var presentationCount = 0;
        using var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, _, _) =>
            {
                Interlocked.Increment(ref presentationCount);
                return Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
            },
            tasks);
        var request = new AppPackageUiRecoveryRequest(
            "agent",
            "hash-a",
            Guid.NewGuid(),
            Task.FromResult(AppPackageUiCleanupOutcome.Unproven),
            CreateFaultNotice(notifications, "agent", "hash-a"));

        Assert.True(coordinator.TrySchedule(request));
        await coordinator.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref presentationCount));
        Assert.Equal(1, notifications.PublishCount);
        Assert.False(coordinator.TrySchedule(request));
    }

    [Fact]
    public async Task Recovery_CleanupTimeoutPublishesWithoutStartingPresentation()
    {
        await using var host = CreateHost();
        var notifications = new CapturingNotificationService();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var cleanup = new TaskCompletionSource<AppPackageUiCleanupOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationCount = 0;
        using var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, _, _) =>
            {
                Interlocked.Increment(ref presentationCount);
                return Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
            },
            tasks,
            new AppPackageUiRecoveryAttemptRegistry(),
            TimeSpan.FromMilliseconds(20));
        var request = new AppPackageUiRecoveryRequest(
            "agent",
            "hash-a",
            Guid.NewGuid(),
            cleanup.Task,
            CreateFaultNotice(notifications, "agent", "hash-a"));

        Assert.True(coordinator.TrySchedule(request));
        await coordinator.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref presentationCount));
        Assert.Equal(1, notifications.PublishCount);
        Assert.False(cleanup.Task.IsCompleted);
        Assert.True(AppCleanupQuarantine.IsRetained(cleanup.Task));
        cleanup.TrySetResult(AppPackageUiCleanupOutcome.Proven);
        await Eventually.ObserveAsync(
            _ => ValueTask.FromResult(AppCleanupQuarantine.IsRetained(cleanup.Task)),
            static retained => !retained,
            TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData((int)AppPackageUiRecoveryPresentationOutcome.Applied, 0)]
    [InlineData((int)AppPackageUiRecoveryPresentationOutcome.ContentChanged, 0)]
    [InlineData((int)AppPackageUiRecoveryPresentationOutcome.Superseded, 0)]
    [InlineData((int)AppPackageUiRecoveryPresentationOutcome.TimedOut, 1)]
    public async Task RecoveryOutcome_PublishesOnlyWhenRecoveryTimesOut(
        int outcomeValue,
        int expectedNotifications)
    {
        var outcome = (AppPackageUiRecoveryPresentationOutcome)outcomeValue;
        await using var host = CreateHost();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var notifications = new CapturingNotificationService();
        using var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, _, _) => Task.FromResult(outcome),
            tasks);
        var request = new AppPackageUiRecoveryRequest(
            "agent",
            "hash-a",
            Guid.NewGuid(),
            Task.FromResult(AppPackageUiCleanupOutcome.Proven),
            CreateFaultNotice(notifications, "agent", "hash-a"));

        Assert.True(coordinator.TrySchedule(request));
        await coordinator.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(expectedNotifications, notifications.PublishCount);
    }

    [Fact]
    public async Task Recovery_FailureIsTerminalForContentButUpdatedContentGetsOneAttempt()
    {
        await using var host = CreateHost();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var attempts = new List<string>();
        using var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, contentHash, _) =>
            {
                attempts.Add(contentHash);
                return contentHash == "hash-a"
                    ? Task.FromException<AppPackageUiRecoveryPresentationOutcome>(
                        new InvalidOperationException("replacement failed"))
                    : Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
            },
            tasks);
        var first = CreateCompletedRequest("hash-a");

        Assert.True(coordinator.TrySchedule(first));
        await coordinator.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(coordinator.TrySchedule(first));

        var updated = CreateCompletedRequest("hash-b");
        Assert.True(coordinator.TrySchedule(updated));
        await coordinator.WaitForRecoveryAsync("agent", "hash-b")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["hash-a", "hash-b"], attempts);
    }

    [Fact]
    public async Task Dispose_CancelsPendingRecoveryBeforePresentation()
    {
        await using var host = CreateHost();
        using var tasks = new OwnedTaskObserver(nameof(AppPackageUiRecoveryCoordinatorTests));
        var cleanup = new TaskCompletionSource<AppPackageUiCleanupOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationCount = 0;
        var coordinator = new AppPackageUiRecoveryCoordinator(
            host,
            (_, _, _) =>
            {
                Interlocked.Increment(ref presentationCount);
                return Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
            },
            tasks);
        var request = new AppPackageUiRecoveryRequest(
            "agent",
            "hash-a",
            Guid.NewGuid(),
            cleanup.Task);
        Assert.True(coordinator.TrySchedule(request));

        coordinator.Dispose();
        await tasks.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref presentationCount));
        Assert.False(coordinator.TrySchedule(CreateCompletedRequest("hash-b")));
    }

    [Fact]
    public async Task AttemptRegistry_SurvivesCoordinatorLifetimeAndNewRegistryResetsIt()
    {
        await using var host = CreateHost();
        using var firstTasks = new OwnedTaskObserver("first shell");
        using var secondTasks = new OwnedTaskObserver("second shell");
        using var restartedTasks = new OwnedTaskObserver("restarted process");
        var processAttempts = new AppPackageUiRecoveryAttemptRegistry();
        var request = CreateCompletedRequest("hash-a");
        var forceCount = 0;
        Task<AppPackageUiRecoveryPresentationOutcome> ForceAsync(
            string _,
            string __,
            CancellationToken ___)
        {
            Interlocked.Increment(ref forceCount);
            return Task.FromResult(AppPackageUiRecoveryPresentationOutcome.Applied);
        }

        using var first = new AppPackageUiRecoveryCoordinator(
            host,
            ForceAsync,
            firstTasks,
            processAttempts);
        Assert.True(first.TrySchedule(request));
        await first.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));
        first.Dispose();

        using var second = new AppPackageUiRecoveryCoordinator(
            host,
            ForceAsync,
            secondTasks,
            processAttempts);
        Assert.False(second.TrySchedule(request));
        second.Dispose();

        using var restarted = new AppPackageUiRecoveryCoordinator(
            host,
            ForceAsync,
            restartedTasks,
            new AppPackageUiRecoveryAttemptRegistry());
        Assert.True(restarted.TrySchedule(request));
        await restarted.WaitForRecoveryAsync("agent", "hash-a")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref forceCount));
    }

    private static PackageViewHostService CreateHost()
        => new(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            sessionFolder: null,
            uiDispatcher: ImmediateUiDispatcher.Instance);

    private static AppPackageUiRecoveryRequest CreateCompletedRequest(string contentHash)
        => new(
            "agent",
            contentHash,
            Guid.NewGuid(),
            Task.FromResult(AppPackageUiCleanupOutcome.Proven));

    private static AppPackageFaultNoticeIncident CreateFaultNotice(
        IPackageNotificationService notifications,
        string packageId,
        string contentHash)
    {
        var coordinator = new AppPackageFaultNoticeCoordinator(notifications);
        return coordinator.Register(
            new AppPackageFaultIdentity(
                packageId,
                "Agent",
                contentHash,
                Guid.NewGuid()),
            AppPackageFaultNoticeKind.Package,
            viewId: null,
            "Agent App UI unavailable",
            "The package App UI is unavailable. Reference: test.",
            "test",
            deferNotification: true).Incident;
    }

    private sealed class CapturingNotificationService : IPackageNotificationService
    {
        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
