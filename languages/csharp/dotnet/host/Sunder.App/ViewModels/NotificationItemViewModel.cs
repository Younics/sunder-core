using Sunder.App.Models;
using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

public sealed class NotificationItemViewModel(AppNotificationRecord notification, DateTimeOffset lastReadAtUtc)
{
    public string NotificationId { get; } = notification.NotificationId;

    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public string SourceText { get; } = $"{(string.IsNullOrWhiteSpace(notification.SourceDisplayName) ? notification.SourcePackageId : notification.SourceDisplayName)} · {notification.CreatedAtUtc.ToLocalTime():g}";

    public bool IsUnread { get; } = notification.CreatedAtUtc > lastReadAtUtc;

    public string SeverityGlyph { get; } = NotificationItemViewModelSeverity.ToGlyph(notification.Severity);

    public string SeverityText { get; } = ToSeverityText(notification.Severity);

    private static string ToSeverityText(PackageNotificationSeverity severity)
        => severity switch
        {
            PackageNotificationSeverity.Success => "Success",
            PackageNotificationSeverity.Warning => "Warning",
            PackageNotificationSeverity.Error => "Error",
            _ => "Info",
        };
}
