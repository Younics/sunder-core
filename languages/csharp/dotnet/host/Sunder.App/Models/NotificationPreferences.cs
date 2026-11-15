using Sunder.Sdk.Notifications;

namespace Sunder.App.Models;

public sealed record NotificationPreferences(
    bool ShowToasts = true,
    bool ShowInformation = true,
    bool ShowSuccess = true,
    bool ShowWarnings = true)
{
    internal bool AllowsToast(PackageNotificationSeverity severity)
        => severity == PackageNotificationSeverity.Error || (ShowToasts && (severity switch
        {
            PackageNotificationSeverity.Information => ShowInformation,
            PackageNotificationSeverity.Success => ShowSuccess,
            PackageNotificationSeverity.Warning => ShowWarnings,
            _ => false,
        }));
}
