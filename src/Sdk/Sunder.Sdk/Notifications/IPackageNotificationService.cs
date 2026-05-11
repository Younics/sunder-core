using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public interface IPackageNotificationService
{
    ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default);
}
