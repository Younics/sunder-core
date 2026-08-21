using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageFaultNotifierTests
{
    [Fact]
    public void QueuePackageDisabled_DefersSanitizedNoticeUntilRecoveryDecision()
    {
        var notifications = new CapturingNotificationService();
        var notifier = new AppPackageFaultNotifier(
            notifications,
            ImmediateUiDispatcher.Instance);
        PackageViewHostFaultEventArgs? fault = null;
        notifier.PackageFaulted += (_, args) => fault = args;

        notifier.QueuePackageDisabled(
            this,
            "private.package.id",
            "private cleanup and recovery details",
            PackageFailureOrigin.AppUnhandledUi,
            new InvalidOperationException("private exception text"));

        Assert.Null(notifications.Request);
        Assert.NotNull(fault);

        notifier.PublishPackageDisabled("private.package.id");

        var request = Assert.IsType<PackageNotificationRequest>(notifications.Request);
        Assert.Equal("private.package.id App UI unavailable", request.Title);
        Assert.StartsWith(
            "A package's App UI became unavailable. Other packages remain available. Reference: ",
            request.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "A package's App UI became unavailable. Other packages remain available.",
            fault.Message);
    }

    [Fact]
    public void SharedCoordinator_CoalescesSameContentAcrossGenerationNotifiers()
    {
        var notifications = new CapturingNotificationService();
        var coordinator = new AppPackageFaultNoticeCoordinator(notifications);
        var first = new AppPackageFaultNotifier(
            coordinator,
            packageId => new AppPackageFaultIdentity(
                packageId,
                "Agent",
                "content-hash",
                Guid.NewGuid()),
            ImmediateUiDispatcher.Instance);
        var second = new AppPackageFaultNotifier(
            coordinator,
            packageId => new AppPackageFaultIdentity(
                packageId,
                "Agent",
                "content-hash",
                Guid.NewGuid()),
            ImmediateUiDispatcher.Instance);

        first.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("first"));
        second.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("second"));

        Assert.Equal(1, notifications.PublishCount);
        Assert.Equal("Agent App UI update failed", notifications.Request?.Title);
    }

    [Fact]
    public void SharedCoordinator_RetriesPublicationAfterRecoverableStoreFailure()
    {
        var notifications = new FailFirstNotificationService();
        var coordinator = new AppPackageFaultNoticeCoordinator(notifications);
        AppPackageFaultIdentity Resolve(string packageId) => new(
            packageId,
            "Agent",
            "content-hash",
            Guid.NewGuid());
        var first = new AppPackageFaultNotifier(
            coordinator,
            Resolve,
            ImmediateUiDispatcher.Instance);
        var second = new AppPackageFaultNotifier(
            coordinator,
            Resolve,
            ImmediateUiDispatcher.Instance);

        first.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("first"));
        second.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("second"));

        Assert.Equal(2, notifications.PublishAttempts);
        Assert.Equal(1, notifications.SuccessfulPublications);
    }

    [Fact]
    public void SharedCoordinator_RetriesLonePublicationAfterRecoverableStoreFailure()
    {
        var notifications = new FailFirstNotificationService();
        var coordinator = new AppPackageFaultNoticeCoordinator(notifications);
        var notifier = new AppPackageFaultNotifier(
            coordinator,
            packageId => new AppPackageFaultIdentity(
                packageId,
                "Agent",
                "content-hash",
                Guid.NewGuid()),
            ImmediateUiDispatcher.Instance);

        notifier.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("first"));

        Assert.Equal(2, notifications.PublishAttempts);
        Assert.Equal(1, notifications.SuccessfulPublications);
    }

    [Fact]
    public async Task SharedCoordinator_RetriesRequestThatArrivesDuringFailedPublication()
    {
        var notifications = new DeferredFailFirstNotificationService();
        var coordinator = new AppPackageFaultNoticeCoordinator(notifications);
        AppPackageFaultIdentity Resolve(string packageId) => new(
            packageId,
            "Agent",
            "content-hash",
            Guid.NewGuid());
        var first = new AppPackageFaultNotifier(
            coordinator,
            Resolve,
            ImmediateUiDispatcher.Instance);
        var second = new AppPackageFaultNotifier(
            coordinator,
            Resolve,
            ImmediateUiDispatcher.Instance);

        first.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("first"));
        second.QueueActivationFailure(
            "agent",
            candidateReplacement: true,
            new InvalidOperationException("second"));
        Assert.Equal(1, notifications.PublishAttempts);

        notifications.FailFirstPublication();
        await TestSupport.AsyncAssert.WaitForConditionAsync(
            () => notifications.PublishAttempts == 2);

        Assert.Equal(1, notifications.SuccessfulPublications);
    }

    [Fact]
    public async Task QueuePackageDisabled_WhenMarshaledSubscriberThrowsFatalFault_ForwardsIt()
    {
        var propagated = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new AppPackageFaultNotifier(
            notificationService: null,
            new FaultCapturingDispatcher(),
            exception => propagated.TrySetResult(exception));
        var fatal = new OutOfMemoryException("fatal subscriber fault");
        notifier.PackageFaulted += (_, _) => throw fatal;

        notifier.QueuePackageDisabled(
            this,
            "agent",
            "Package failed.",
            PackageFailureOrigin.AppUnhandledUi,
            exception: null);

        Assert.Same(
            fatal,
            await propagated.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class FaultCapturingDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private sealed class CapturingNotificationService : IPackageNotificationService
    {
        public PackageNotificationRequest? Request { get; private set; }

        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            PublishCount++;
            Request = request;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailFirstNotificationService : IPackageNotificationService
    {
        public int PublishAttempts { get; private set; }
        public int SuccessfulPublications { get; private set; }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishAttempts++;
            if (PublishAttempts == 1)
            {
                throw new InvalidOperationException("notification store unavailable");
            }

            SuccessfulPublications++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredFailFirstNotificationService : IPackageNotificationService
    {
        private readonly TaskCompletionSource _firstPublication = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int PublishAttempts { get; private set; }
        public int SuccessfulPublications { get; private set; }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishAttempts++;
            if (PublishAttempts == 1)
            {
                return new ValueTask(_firstPublication.Task);
            }

            SuccessfulPublications++;
            return ValueTask.CompletedTask;
        }

        public void FailFirstPublication()
            => _firstPublication.TrySetException(
                new InvalidOperationException("notification store unavailable"));
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
