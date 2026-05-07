namespace Sunder.Sdk.Notifications;

public sealed class NullPackageNotificationService : IPackageNotificationService
{
    public static NullPackageNotificationService Instance { get; } = new();

    private NullPackageNotificationService()
    {
    }

    public ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
