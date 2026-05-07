using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class AppPackageNotificationService(
    NotificationCenterService notificationCenter,
    string sourcePackageId,
    string sourceDisplayName) : IPackageNotificationService
{
    private readonly NotificationCenterService _notificationCenter = notificationCenter;
    private readonly string _sourcePackageId = sourcePackageId;
    private readonly string _sourceDisplayName = sourceDisplayName;

    public ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default)
        => _notificationCenter.PublishAsync(_sourcePackageId, _sourceDisplayName, request, cancellationToken);
}
