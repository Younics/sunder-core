using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public enum PackageNotificationDisplayMode
{
    TrayOnly = 0,
    ToastAndTray = 1,
    ToastOnly = 2,
}
