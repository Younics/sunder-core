namespace Sunder.Sdk.Notifications;

public sealed record PackageNotificationRequest(
    string Title,
    string Message,
    PackageNotificationDisplayMode DisplayMode = PackageNotificationDisplayMode.ToastAndTray,
    PackageNotificationSeverity Severity = PackageNotificationSeverity.Information);
