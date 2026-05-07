namespace Sunder.Sdk.Notifications;

public interface IPackageNotificationService
{
    ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default);
}
