using Sunder.App.Models;
using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

public sealed class ToastNotificationViewModel(AppToastNotification notification)
{
    public string NotificationId { get; } = notification.NotificationId;

    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public string SourceText { get; } = string.IsNullOrWhiteSpace(notification.SourceDisplayName)
        ? notification.SourcePackageId
        : notification.SourceDisplayName;

    public string SeverityGlyph { get; } = NotificationItemViewModelSeverity.ToGlyph(notification.Severity);
}
