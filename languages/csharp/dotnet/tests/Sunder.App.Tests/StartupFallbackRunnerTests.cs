using Sunder.App.Services;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class StartupFallbackRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenNormalStartupSucceeds_DoesNotFallbackOrNotify()
    {
        var calls = new List<string>();

        var outcome = await StartupFallbackRunner.RunAsync(
            (openCoreShell, _) =>
            {
                calls.Add(openCoreShell ? "core" : "normal");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                calls.Add("notify");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["normal"], calls);
        Assert.Null(outcome.StartupFailure);
        Assert.Null(outcome.CoreShellFailure);
    }

    [Fact]
    public async Task RunAsync_WhenNormalStartupFails_OpensCoreThenNotifies()
    {
        var startupFailure = new InvalidOperationException("Runtime failed.");
        var calls = new List<string>();

        var outcome = await StartupFallbackRunner.RunAsync(
            (openCoreShell, _) =>
            {
                calls.Add(openCoreShell ? "core" : "normal");
                return openCoreShell
                    ? Task.CompletedTask
                    : Task.FromException(startupFailure);
            },
            (failure, _) =>
            {
                Assert.Same(startupFailure, failure);
                calls.Add("notify");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["normal", "core", "notify"], calls);
        Assert.Same(startupFailure, outcome.StartupFailure);
        Assert.Null(outcome.CoreShellFailure);
    }

    [Fact]
    public async Task RunAsync_WhenCoreShellFails_StopsAfterTheSecondAttempt()
    {
        var startupFailure = new InvalidOperationException("Runtime failed.");
        var coreShellFailure = new InvalidOperationException("Core failed.");
        var calls = new List<string>();

        var outcome = await StartupFallbackRunner.RunAsync(
            (openCoreShell, _) =>
            {
                calls.Add(openCoreShell ? "core" : "normal");
                return Task.FromException(
                    openCoreShell ? coreShellFailure : startupFailure
                );
            },
            (_, _) =>
            {
                calls.Add("notify");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["normal", "core"], calls);
        Assert.Same(startupFailure, outcome.StartupFailure);
        Assert.Same(coreShellFailure, outcome.CoreShellFailure);
    }

    [Fact]
    public async Task RunAsync_WhenApplicationIsCancelled_DoesNotStartCoreShell()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StartupFallbackRunner.RunAsync(
                (_, _) =>
                {
                    attempts++;
                    cancellation.Cancel();
                    return Task.FromException(new InvalidOperationException("Runtime failed."));
                },
                (_, _) => Task.CompletedTask,
                cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PublishStartupFailureAsync_UsesSdkToastAndTrayErrorNotification()
    {
        var notificationService = new RecordingNotificationService();

        await Sunder.App.App.PublishStartupFailureAsync(
            notificationService,
            new InvalidOperationException("Runtime failed."));

        var request = Assert.IsType<PackageNotificationRequest>(notificationService.Request);
        Assert.Equal("Sunder startup failed", request.Title);
        Assert.Equal("Runtime failed.", request.Message);
        Assert.Equal(PackageNotificationDisplayMode.ToastAndTray, request.DisplayMode);
        Assert.Equal(PackageNotificationSeverity.Error, request.Severity);
    }

    [Fact]
    public async Task PublishStartupFailureAsync_WhenSdkNotificationFails_DoesNotFailTheOpenShell()
    {
        await Sunder.App.App.PublishStartupFailureAsync(
            new ThrowingNotificationService(),
            new InvalidOperationException("Runtime failed."));
    }

    [Fact]
    public void ResolveStartupNotificationService_WhenResolutionFails_UsesNullSdkService()
    {
        var notificationService = Sunder.App.App.ResolveStartupNotificationService(
            new ThrowingServiceProvider());

        Assert.Same(NullPackageNotificationService.Instance, notificationService);
    }

    private sealed class RecordingNotificationService : IPackageNotificationService
    {
        public PackageNotificationRequest? Request { get; private set; }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingNotificationService : IPackageNotificationService
    {
        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default
        ) => new(Task.FromException(new IOException("Notification storage failed.")));
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("Service resolution failed.");
    }
}
