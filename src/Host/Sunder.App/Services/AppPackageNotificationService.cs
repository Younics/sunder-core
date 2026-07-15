using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class AppPackageNotificationService : IPackageNotificationService
{
    private readonly NotificationCenterService _notificationCenter;
    private readonly string _sourcePackageId;
    private readonly string _sourceDisplayName;
    private readonly AppPackageGenerationPublication? _publication;

    public AppPackageNotificationService(
        NotificationCenterService notificationCenter,
        string sourcePackageId,
        string sourceDisplayName)
        : this(notificationCenter, sourcePackageId, sourceDisplayName, publication: null)
    {
    }

    internal AppPackageNotificationService(
        NotificationCenterService notificationCenter,
        string sourcePackageId,
        string sourceDisplayName,
        AppPackageGenerationPublication? publication)
    {
        _notificationCenter = notificationCenter;
        _sourcePackageId = sourcePackageId;
        _sourceDisplayName = sourceDisplayName;
        _publication = publication;
    }

    public ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_publication is null)
        {
            return _notificationCenter.PublishAsync(_sourcePackageId, _sourceDisplayName, request, cancellationToken);
        }

        var snapshot = request with { };
        _publication.Buffer(() =>
            _notificationCenter.PublishAsync(
                _sourcePackageId,
                _sourceDisplayName,
                snapshot,
                CancellationToken.None).GetAwaiter().GetResult());
        return ValueTask.CompletedTask;
    }
}
