using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

/// <summary>Defines one user-visible package notification.</summary>
/// <param name="Title">Short user-facing heading.</param>
/// <param name="Message">User-facing detail text.</param>
/// <param name="DisplayMode">Requested host surfaces; defaults to toast and tray.</param>
/// <param name="Severity">Visual and accessibility severity; defaults to information.</param>
[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public sealed record PackageNotificationRequest(
    string Title,
    string Message,
    PackageNotificationDisplayMode DisplayMode = PackageNotificationDisplayMode.ToastAndTray,
    PackageNotificationSeverity Severity = PackageNotificationSeverity.Information);
