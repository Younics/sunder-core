using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public sealed record PackageNotificationRequest(
    string Title,
    string Message,
    PackageNotificationDisplayMode DisplayMode = PackageNotificationDisplayMode.ToastAndTray,
    PackageNotificationSeverity Severity = PackageNotificationSeverity.Information);
