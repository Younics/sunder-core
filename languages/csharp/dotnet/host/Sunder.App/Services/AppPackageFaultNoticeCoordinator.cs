using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal enum AppPackageFaultNoticeKind
{
    View,
    Package,
    Activation,
}

internal sealed record AppPackageFaultIdentity(
    string PackageId,
    string DisplayName,
    string? ContentHash,
    Guid GenerationId);

internal readonly record struct AppPackageFaultNoticeRegistration(
    AppPackageFaultNoticeIncident Incident,
    bool ShouldPublishImmediately);

internal sealed class AppPackageFaultNoticeIncident(
    AppPackageFaultNoticeCoordinator owner,
    string key,
    string packageId,
    Guid generationId,
    string correlationId,
    PackageNotificationRequest request)
{
    private int _state;
    private int _deferred;
    private int _publicationRetryRequested;
    private int _automaticPublicationRetryConsumed;

    internal string Key { get; } = key;
    internal string PackageId { get; } = packageId;
    internal Guid GenerationId { get; } = generationId;
    internal string CorrelationId { get; } = correlationId;
    internal PackageNotificationRequest Request { get; } = request;
    internal bool IsResolved => Volatile.Read(ref _state) == 3;
    internal bool IsPending => Volatile.Read(ref _state) == 0;
    internal bool IsPublishing => Volatile.Read(ref _state) == 1;
    internal bool IsDeferred => Volatile.Read(ref _deferred) != 0;

    internal void Defer() => Volatile.Write(ref _deferred, 1);

    internal void RequirePublication() => Volatile.Write(ref _deferred, 0);

    internal bool TryBeginPublication()
        => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal void CompletePublication()
    {
        Interlocked.Exchange(ref _publicationRetryRequested, 0);
        Interlocked.CompareExchange(ref _state, 2, 1);
    }

    internal void ResetPublication()
        => Interlocked.CompareExchange(ref _state, 0, 1);

    internal void RequestPublicationRetry()
        => Volatile.Write(ref _publicationRetryRequested, 1);

    internal bool ConsumePublicationRetry()
    {
        var retryRequested = Interlocked.Exchange(ref _publicationRetryRequested, 0) != 0;
        var automaticRetryAvailable = Interlocked.Exchange(
            ref _automaticPublicationRetryConsumed,
            1) == 0;
        return retryRequested || automaticRetryAvailable;
    }

    internal bool TryMarkResolved()
        => Interlocked.CompareExchange(ref _state, 3, 0) == 0;

    public void Publish() => owner.Publish(this);

    public void Resolve() => owner.Resolve(this);
}

internal sealed class AppPackageFaultNoticeCoordinator(
    IPackageNotificationService? notificationService,
    Action<Exception>? fatalExceptionHandler = null)
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AppPackageFaultNoticeIncident> _incidents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<Exception> _fatalExceptionHandler = fatalExceptionHandler
        ?? AppPackageFatalCleanupPropagation.PostToDispatcher;

    public AppPackageFaultNoticeRegistration Register(
        AppPackageFaultIdentity identity,
        AppPackageFaultNoticeKind kind,
        string? viewId,
        string title,
        string message,
        string correlationId,
        bool deferNotification)
    {
        var key = CreateKey(identity, kind, viewId);
        lock (_syncRoot)
        {
            if (_incidents.TryGetValue(key, out var existing)
                && !existing.IsResolved)
            {
                if (existing.GenerationId != identity.GenerationId
                    && existing.IsDeferred
                    && existing.IsPending)
                {
                    return RegisterNewIncident();
                }
                if (!deferNotification
                    && !existing.IsDeferred
                    && existing.IsPublishing)
                {
                    existing.RequestPublicationRetry();
                }
                return new AppPackageFaultNoticeRegistration(
                    existing,
                    ShouldPublishImmediately: !deferNotification
                        && existing.IsPending
                        && !existing.IsDeferred);
            }

            return RegisterNewIncident();

            AppPackageFaultNoticeRegistration RegisterNewIncident()
            {
                var incident = new AppPackageFaultNoticeIncident(
                    this,
                    key,
                    identity.PackageId,
                    identity.GenerationId,
                    correlationId,
                    new PackageNotificationRequest(
                        title,
                        message,
                        PackageNotificationDisplayMode.ToastAndTray,
                        PackageNotificationSeverity.Error));
                if (deferNotification)
                {
                    incident.Defer();
                }
                _incidents[key] = incident;
                return new AppPackageFaultNoticeRegistration(
                    incident,
                    ShouldPublishImmediately: !deferNotification);
            }
        }
    }

    internal void Publish(AppPackageFaultNoticeIncident incident)
    {
        incident.RequirePublication();
        lock (_syncRoot)
        {
            if (!_incidents.TryGetValue(incident.Key, out var current)
                || !ReferenceEquals(current, incident)
                || !incident.TryBeginPublication())
            {
                if (ReferenceEquals(current, incident) && incident.IsPublishing)
                {
                    incident.RequestPublicationRetry();
                }
                return;
            }
        }

        if (notificationService is null)
        {
            incident.CompletePublication();
            return;
        }

        try
        {
            var publication = notificationService.PublishAsync(incident.Request);
            if (publication.IsCompletedSuccessfully)
            {
                incident.CompletePublication();
            }
            else
            {
                _ = ObservePublicationAsync(publication, incident);
            }
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Failed to queue package fault notification for '{incident.PackageId}'.",
                exception);
            RetryFailedPublication(incident);
        }
    }

    internal void Resolve(AppPackageFaultNoticeIncident incident)
    {
        lock (_syncRoot)
        {
            if (_incidents.TryGetValue(incident.Key, out var current)
                && ReferenceEquals(current, incident))
            {
                incident.TryMarkResolved();
            }
        }
    }

    private async Task ObservePublicationAsync(
        ValueTask publication,
        AppPackageFaultNoticeIncident incident)
    {
        try
        {
            await publication.ConfigureAwait(false);
            incident.CompletePublication();
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                $"Failed to publish package fault notification for '{incident.PackageId}'.",
                exception);
            RetryFailedPublication(incident);
        }
        catch (Exception exception)
        {
            incident.ResetPublication();
            _fatalExceptionHandler(exception);
        }
    }

    private void RetryFailedPublication(AppPackageFaultNoticeIncident incident)
    {
        incident.ResetPublication();
        if (incident.ConsumePublicationRetry())
        {
            Publish(incident);
        }
    }

    private static string CreateKey(
        AppPackageFaultIdentity identity,
        AppPackageFaultNoticeKind kind,
        string? viewId)
    {
        var contentKey = string.IsNullOrWhiteSpace(identity.ContentHash)
            ? identity.GenerationId.ToString("N")
            : identity.ContentHash.Trim();
        var surface = kind == AppPackageFaultNoticeKind.View
            ? $"view:{viewId?.Trim()}"
            : "package";
        return $"{identity.PackageId.Trim()}\0{contentKey}\0{surface}";
    }
}
