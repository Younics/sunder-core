using Sunder.App.Services;
using Sunder.App.Models;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimePackageRecoveryNotificationServiceTests
{
    [Fact]
    public async Task LiveRecoveryEvents_MapToPersistentAndTransientHostNotifications()
    {
        var notifications = new NotificationCenterService(
            Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new RuntimePackageRecoveryNotificationService(notifications);
        var runtimeInstanceId = Guid.NewGuid();
        var toasts = new List<AppToastNotification>();
        notifications.ToastQueued += toasts.Add;
        service.SeedSilently(runtimeInstanceId, 10);

        await service.HandleAsync(CreateEvent(runtimeInstanceId, 11, RuntimeEventKind.PackageRecoveryStarted));
        await service.HandleAsync(CreateEvent(runtimeInstanceId, 12, RuntimeEventKind.PackageRecoverySucceeded));
        await service.HandleAsync(CreateEvent(runtimeInstanceId, 13, RuntimeEventKind.PackageRecoverySuspended));

        var suspended = Assert.Single(notifications.ListNotifications());
        Assert.Equal(PackageNotificationSeverity.Error, suspended.Severity);
        Assert.Contains("suspended", suspended.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, toasts.Count);
        Assert.Equal(PackageNotificationSeverity.Warning, toasts[0].Severity);
        Assert.Equal(PackageNotificationSeverity.Information, toasts[1].Severity);
        Assert.DoesNotContain(
            notifications.ListNotifications(),
            notification => notification.Title.Contains("restarted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DuplicateAndReconnectReplay_AreSeededSilently()
    {
        var notifications = new NotificationCenterService(
            Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new RuntimePackageRecoveryNotificationService(notifications);
        var runtimeInstanceId = Guid.NewGuid();
        var toastCount = 0;
        notifications.ToastQueued += _ => toastCount++;
        service.SeedSilently(runtimeInstanceId, 20);
        var live = CreateEvent(runtimeInstanceId, 21, RuntimeEventKind.PackageRecoveryStarted);

        await service.HandleAsync(live);
        await service.HandleAsync(live);
        service.SeedSilently(runtimeInstanceId, 25);
        await service.HandleAsync(CreateEvent(runtimeInstanceId, 24, RuntimeEventKind.PackageRecoverySuspended));
        await service.HandleAsync(CreateEvent(runtimeInstanceId, 25, RuntimeEventKind.PackageRecoverySuspended));

        Assert.Equal(1, toastCount);
        Assert.Empty(notifications.ListNotifications());
    }

    [Fact]
    public async Task HistoryGapAndRuntimeReplacement_DoNotReplayRecoveryNotifications()
    {
        var notifications = new NotificationCenterService(
            Path.Combine(CreateTempDirectory(), "notifications.json"));
        var service = new RuntimePackageRecoveryNotificationService(notifications);
        var firstRuntime = Guid.NewGuid();
        var replacementRuntime = Guid.NewGuid();
        var toastCount = 0;
        notifications.ToastQueued += _ => toastCount++;
        service.SeedSilently(firstRuntime, 100);

        await service.HandleAsync(CreateEvent(firstRuntime, 99, RuntimeEventKind.PackageRecoverySuspended));
        service.SeedSilently(replacementRuntime, 7);
        await service.HandleAsync(CreateEvent(firstRuntime, 101, RuntimeEventKind.PackageRecoveryStarted));
        await service.HandleAsync(CreateEvent(replacementRuntime, 7, RuntimeEventKind.PackageRecoverySuspended));
        await service.HandleAsync(CreateEvent(replacementRuntime, 8, RuntimeEventKind.PackageRecoverySucceeded));

        Assert.Empty(notifications.ListNotifications());
        Assert.Equal(1, toastCount);
    }

    private static RuntimeEventDescriptor CreateEvent(
        Guid runtimeInstanceId,
        long sequenceId,
        RuntimeEventKind kind)
        => new(
            runtimeInstanceId,
            sequenceId,
            DateTimeOffset.UtcNow,
            kind,
            SessionGeneration: 3,
            RuntimeOperationPhase.Idle,
            RuntimeBootstrapState.Ready,
            ["test.package"],
            kind == RuntimeEventKind.PackageRecoverySucceeded
                ? true
                : kind == RuntimeEventKind.PackageRecoverySuspended
                    ? false
                    : null,
            $"Recovery event {kind}.");
}
