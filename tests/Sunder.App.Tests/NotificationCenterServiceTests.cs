using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class NotificationCenterServiceTests
{
    [Fact]
    public async Task PublishAsync_TrayNotification_PersistsUnreadRecord()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest(
                "Build complete",
                "All checks passed.",
                PackageNotificationDisplayMode.TrayOnly,
                PackageNotificationSeverity.Success));

        var notification = Assert.Single(service.ListNotifications());
        Assert.Equal("Build complete", notification.Title);
        Assert.True(service.HasUnreadTrayNotifications());

        var reloaded = new NotificationCenterService(statePath);
        var reloadedNotification = Assert.Single(reloaded.ListNotifications());
        Assert.Equal(notification.NotificationId, reloadedNotification.NotificationId);
        Assert.True(reloaded.HasUnreadTrayNotifications());
    }

    [Fact]
    public async Task PublishAsync_ToastOnly_DoesNotPersistTrayNotification()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);
        AppToastNotification? queuedToast = null;
        service.ToastQueued += notification => queuedToast = notification;

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest(
                "Copied",
                "Copied response to clipboard.",
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Success));

        Assert.NotNull(queuedToast);
        Assert.Empty(service.ListNotifications());
        Assert.False(service.HasUnreadTrayNotifications());
        Assert.False(File.Exists(statePath));

        var reloaded = new NotificationCenterService(statePath);
        Assert.Empty(reloaded.ListNotifications());
        Assert.False(reloaded.HasUnreadTrayNotifications());
    }

    [Fact]
    public async Task PublishAsync_WhenSubscribersThrow_NotifiesRemainingSubscribers()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);
        var notificationChangedCount = 0;
        AppToastNotification? queuedToast = null;
        service.NotificationsChanged += () => throw new InvalidOperationException("notification subscriber failed");
        service.NotificationsChanged += () => notificationChangedCount++;
        service.ToastQueued += _ => throw new InvalidOperationException("toast subscriber failed");
        service.ToastQueued += notification => queuedToast = notification;

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest(
                "Build complete",
                "All checks passed.",
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Success));

        Assert.Equal(1, notificationChangedCount);
        Assert.NotNull(queuedToast);
        Assert.Single(service.ListNotifications());
    }

    [Fact]
    public async Task PublishAsync_TrayNotification_DoesNotLeaveTemporaryStateFiles()
    {
        var rootPath = CreateTempDirectory();
        var statePath = Path.Combine(rootPath, "notifications.json");
        var service = new NotificationCenterService(statePath);

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest(
                "Build complete",
                "All checks passed.",
                PackageNotificationDisplayMode.TrayOnly,
                PackageNotificationSeverity.Success));

        Assert.True(File.Exists(statePath));
        Assert.Empty(Directory.EnumerateFiles(rootPath, "notifications.json.*.tmp"));
    }

    [Fact]
    public async Task MarkAllRead_PersistsTimestampAndClearsUnreadAfterReload()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest("Package loaded", "Agent is ready."));
        Assert.True(service.HasUnreadTrayNotifications());

        var readAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        service.MarkAllRead(readAtUtc);

        Assert.Equal(readAtUtc, service.LastReadAtUtc);
        Assert.False(service.HasUnreadTrayNotifications());

        var reloaded = new NotificationCenterService(statePath);
        Assert.Equal(readAtUtc, reloaded.LastReadAtUtc);
        Assert.False(reloaded.HasUnreadTrayNotifications());
    }

    [Fact]
    public async Task ClearAll_RemovesPersistedNotificationsAndClearsUnreadAfterReload()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);

        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest("Package loaded", "Agent is ready."));
        Assert.NotEmpty(service.ListNotifications());

        var clearedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        service.ClearAll(clearedAtUtc);

        Assert.Empty(service.ListNotifications());
        Assert.Equal(clearedAtUtc, service.LastReadAtUtc);
        Assert.False(service.HasUnreadTrayNotifications());

        var reloaded = new NotificationCenterService(statePath);
        Assert.Empty(reloaded.ListNotifications());
        Assert.Equal(clearedAtUtc, reloaded.LastReadAtUtc);
        Assert.False(reloaded.HasUnreadTrayNotifications());
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
