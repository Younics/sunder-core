using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class NotificationCenterServiceTests
{
    [Theory]
    [InlineData(PackageNotificationSeverity.Information, true, false, false)]
    [InlineData(PackageNotificationSeverity.Success, true, false, false)]
    [InlineData(PackageNotificationSeverity.Warning, true, false, false)]
    [InlineData(PackageNotificationSeverity.Error, true, false, true)]
    [InlineData(PackageNotificationSeverity.Information, false, true, false)]
    [InlineData(PackageNotificationSeverity.Success, false, true, false)]
    [InlineData(PackageNotificationSeverity.Warning, false, true, false)]
    [InlineData(PackageNotificationSeverity.Error, false, true, true)]
    public async Task PreferencesControlPopupsButPreserveErrorsAndTrayHistory(
        PackageNotificationSeverity severity, bool showToasts, bool showCategory, bool expectedToast)
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);
        await service.PublishAsync("agent", "Agent", new PackageNotificationRequest("Existing", "Keep history."));
        var preferences = new NotificationPreferences(showToasts, showCategory, showCategory, showCategory);
        service.SavePreferences(preferences);
        service = new NotificationCenterService(statePath);
        Assert.Equal(preferences, service.Preferences);
        var toasts = new List<AppToastNotification>();
        service.ToastQueued += toasts.Add;

        await service.PublishAsync("agent", "Agent", new PackageNotificationRequest("New", "Result", Severity: severity));

        Assert.Equal(expectedToast ? 1 : 0, toasts.Count);
        Assert.Equal(2, service.ListNotifications().Count);
        service.ClearAll();
        Assert.Equal(preferences, new NotificationCenterService(statePath).Preferences);
    }

    [Fact]
    public void SavePreferences_WhenPersistenceFails_KeepsEffectivePreferencesAndCleansTemporaryFile()
    {
        var root = CreateTempDirectory();
        var statePath = Path.Combine(root, "notifications.json");
        var service = new NotificationCenterService(statePath);
        var before = service.Preferences;
        Directory.CreateDirectory(statePath);

        var error = Record.Exception(() => service.SavePreferences(new NotificationPreferences(ShowToasts: false)));

        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a filesystem failure, received: {error}");
        Assert.Equal(before, service.Preferences);
        Assert.Empty(Directory.EnumerateFiles(root, "notifications.json.*.tmp"));
    }

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
    public async Task PublishAsync_WhenSubscriberThrowsFatalFault_DoesNotSwallowIt()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "notifications.json");
        var service = new NotificationCenterService(statePath);
        service.ToastQueued += _ => throw new OutOfMemoryException("fatal subscriber fault");

        await Assert.ThrowsAsync<OutOfMemoryException>(() => service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest(
                "Fault",
                "Fault",
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Error)).AsTask());
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
    public async Task PublishAsync_WhenPersistenceFails_RollsBackInMemoryRecord()
    {
        var rootPath = CreateTempDirectory();
        var statePath = Path.Combine(rootPath, "notifications.json");
        var service = new NotificationCenterService(statePath);
        await service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest("First", "Persisted."));
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);

        var changed = 0;
        service.NotificationsChanged += () => changed++;
        var error = await Record.ExceptionAsync(() => service.PublishAsync(
            "agent",
            "Agent",
            new PackageNotificationRequest("Second", "Must roll back.")).AsTask());

        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a filesystem failure, received: {error}");
        Assert.Equal("First", Assert.Single(service.ListNotifications()).Title);
        Assert.Equal(0, changed);
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

}
