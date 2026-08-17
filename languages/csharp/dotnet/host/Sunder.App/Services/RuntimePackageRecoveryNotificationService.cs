using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class RuntimePackageRecoveryNotificationService(
    NotificationCenterService notificationCenter)
{
    private const int MaximumRememberedEvents = 512;
    private readonly object _syncRoot = new();
    private readonly HashSet<(Guid RuntimeInstanceId, long SequenceId)> _seen = [];
    private readonly Queue<(Guid RuntimeInstanceId, long SequenceId)> _seenOrder = [];
    private Guid _runtimeInstanceId;
    private long _silentSequenceFloor;

    public void SeedSilently(Guid runtimeInstanceId, long sequenceId)
    {
        if (runtimeInstanceId == Guid.Empty || sequenceId < 0)
        {
            return;
        }
        lock (_syncRoot)
        {
            if (_runtimeInstanceId != runtimeInstanceId)
            {
                _runtimeInstanceId = runtimeInstanceId;
                _silentSequenceFloor = sequenceId;
                _seen.Clear();
                _seenOrder.Clear();
            }
            else
            {
                _silentSequenceFloor = Math.Max(_silentSequenceFloor, sequenceId);
            }
        }
    }

    public async ValueTask HandleAsync(
        RuntimeEventDescriptor runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        PackageNotificationRequest? request;
        lock (_syncRoot)
        {
            if (runtimeEvent.RuntimeInstanceId != _runtimeInstanceId
                || runtimeEvent.SequenceId <= _silentSequenceFloor
                || !_seen.Add((runtimeEvent.RuntimeInstanceId, runtimeEvent.SequenceId)))
            {
                return;
            }
            _seenOrder.Enqueue((runtimeEvent.RuntimeInstanceId, runtimeEvent.SequenceId));
            while (_seenOrder.Count > MaximumRememberedEvents)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }
            request = CreateRequest(runtimeEvent);
        }
        if (request is null)
        {
            return;
        }

        try
        {
            await notificationCenter.PublishAsync(
                "sunder.runtime",
                "Sunder Runtime",
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError("Failed to publish a Runtime package recovery notification.", exception);
        }
    }

    private static PackageNotificationRequest? CreateRequest(RuntimeEventDescriptor runtimeEvent)
    {
        var packageId = runtimeEvent.PackageIds.FirstOrDefault() ?? "package";
        return runtimeEvent.Kind switch
        {
            RuntimeEventKind.PackageRecoveryStarted => new PackageNotificationRequest(
                "Package restarting",
                runtimeEvent.Message ?? $"The Runtime is restarting '{packageId}' after a worker failure.",
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Warning),
            RuntimeEventKind.PackageRecoverySucceeded => new PackageNotificationRequest(
                "Package restarted",
                runtimeEvent.Message ?? $"'{packageId}' restarted successfully.",
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Information),
            RuntimeEventKind.PackageRecoverySuspended => new PackageNotificationRequest(
                "Package restart suspended",
                runtimeEvent.Message ?? $"'{packageId}' could not be restarted and remains unavailable.",
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Error),
            _ => null,
        };
    }
}
