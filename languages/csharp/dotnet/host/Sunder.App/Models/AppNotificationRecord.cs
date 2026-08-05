using Sunder.Sdk.Notifications;

namespace Sunder.App.Models;

public sealed record AppNotificationRecord(
    string NotificationId,
    string SourcePackageId,
    string SourceDisplayName,
    string Title,
    string Message,
    PackageNotificationSeverity Severity,
    DateTimeOffset CreatedAtUtc);

public sealed record AppToastNotification(
    string NotificationId,
    string SourcePackageId,
    string SourceDisplayName,
    string Title,
    string Message,
    PackageNotificationSeverity Severity,
    DateTimeOffset CreatedAtUtc);
