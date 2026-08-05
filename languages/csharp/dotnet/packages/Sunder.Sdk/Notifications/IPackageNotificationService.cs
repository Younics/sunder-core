using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Notifications;

/// <summary>Publishes user-visible notifications through host-owned UI surfaces.</summary>
[SunderSdkCapability(SunderSdkCapabilities.NotificationsV1)]
public interface IPackageNotificationService
{
    /// <summary>Queues a copied notification request; cancellation can prevent queuing but does not retract a published notification.</summary>
    ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default);
}
