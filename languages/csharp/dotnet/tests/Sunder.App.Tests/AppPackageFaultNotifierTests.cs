using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageFaultNotifierTests
{
    [Fact]
    public void QueuePackageDisabled_PublishesOnlyFixedRecoveryNeutralText()
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

        var request = Assert.IsType<PackageNotificationRequest>(notifications.Request);
        Assert.Equal(AppPackageFaultNotifier.PackageNotificationTitle, request.Title);
        Assert.StartsWith(
            "A package's App UI became unavailable. Other packages remain available. Reference: ",
            request.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", request.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(fault);
        Assert.Equal(
            "A package's App UI became unavailable. Other packages remain available.",
            fault.Message);
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

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
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
