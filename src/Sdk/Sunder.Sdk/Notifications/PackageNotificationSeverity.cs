using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public enum PackageNotificationSeverity
{
    Information = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}
