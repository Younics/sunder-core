using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

internal static class NotificationItemViewModelSeverity
{
    public static string ToGlyph(PackageNotificationSeverity severity)
        => severity switch
        {
            PackageNotificationSeverity.Success => "✓",
            PackageNotificationSeverity.Warning => "!",
            PackageNotificationSeverity.Error => "!",
            _ => "i",
        };
}
