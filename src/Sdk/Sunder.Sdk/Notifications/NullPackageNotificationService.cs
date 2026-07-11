namespace Sunder.Sdk.Notifications;

/// <summary>Discards notifications where user-visible surfaces are unavailable.</summary>
public sealed class NullPackageNotificationService : IPackageNotificationService
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageNotificationService Instance { get; } = new();

    private NullPackageNotificationService()
    {
    }

    /// <summary>Observes cancellation and otherwise completes without side effects.</summary>
    public ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
