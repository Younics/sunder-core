using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

/// <summary>Specifies which host notification surfaces receive a request.</summary>
[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public enum PackageNotificationDisplayMode
{
    /// <summary>Adds a persistent tray entry without interrupting the user.</summary>
    TrayOnly = 0,
    /// <summary>Shows a transient toast and retains a tray entry.</summary>
    ToastAndTray = 1,
    /// <summary>Shows only a transient toast.</summary>
    ToastOnly = 2,
}
