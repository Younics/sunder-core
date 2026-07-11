using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

/// <summary>Specifies notification meaning and host presentation priority.</summary>
[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public enum PackageNotificationSeverity
{
    /// <summary>Neutral status or guidance.</summary>
    Information = 0,
    /// <summary>Successful completion of a requested operation.</summary>
    Success = 1,
    /// <summary>A recoverable condition requiring attention.</summary>
    Warning = 2,
    /// <summary>An operation failure requiring user awareness.</summary>
    Error = 3,
}
