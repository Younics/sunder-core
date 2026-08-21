using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed class AppPackageFaultNotifier
{
    internal const string ViewNotificationTitle = "Package view unavailable";
    internal const string PackageNotificationTitle = "Package App UI unavailable";

    private const string ViewNotificationMessage =
        "A package view was disabled for this app session. Other views remain available.";
    private const string PackageNotificationMessage =
        "A package's App UI became unavailable. Other packages remain available.";
    private const string InitialActivationNotificationMessage =
        "A package's App UI could not be activated. Other packages remain available.";
    private const string CandidateActivationNotificationMessage =
        "A package App UI update could not be activated. The existing UI remains active.";

    private readonly object _notificationSyncRoot = new();
    private readonly HashSet<string> _reportedFaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppPackageFaultNoticeIncident> _packageIncidents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppPackageFaultNoticeCoordinator _noticeCoordinator;
    private readonly Func<string, AppPackageFaultIdentity> _resolveIdentity;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Action<Exception> _fatalExceptionHandler;

    public AppPackageFaultNotifier(
        IPackageNotificationService? notificationService,
        IUiDispatcher? uiDispatcher = null,
        Action<Exception>? fatalExceptionHandler = null)
        : this(
            new AppPackageFaultNoticeCoordinator(notificationService, fatalExceptionHandler),
            packageId => new AppPackageFaultIdentity(
                packageId,
                packageId,
                ContentHash: null,
                GenerationId: Guid.Empty),
            uiDispatcher,
            fatalExceptionHandler)
    {
    }

    internal AppPackageFaultNotifier(
        AppPackageFaultNoticeCoordinator noticeCoordinator,
        Func<string, AppPackageFaultIdentity> resolveIdentity,
        IUiDispatcher? uiDispatcher = null,
        Action<Exception>? fatalExceptionHandler = null)
    {
        _noticeCoordinator = noticeCoordinator;
        _resolveIdentity = resolveIdentity;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        _fatalExceptionHandler = fatalExceptionHandler
            ?? AppPackageFatalCleanupPropagation.PostToDispatcher;
    }

    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted;

    public void QueueViewQuarantined(
        object sender,
        string packageId,
        string viewId,
        Exception exception)
        => QueueFault(
            sender,
            $"view:{packageId}:{viewId}",
            packageId,
            viewId,
            ViewNotificationTitle,
            ViewNotificationMessage,
            $"Quarantined hosted package view '{viewId}'.",
            PackageFailureOrigin.AppHostedView,
            exception,
            raiseFaultEvent: true,
            deferNotification: false);

    public void QueuePackageDisabled(
        object sender,
        string packageId,
        string details,
        PackageFailureOrigin origin,
        Exception? exception)
        => QueueFault(
            sender,
            $"package:{packageId}",
            packageId,
            viewId: null,
            PackageNotificationTitle,
            PackageNotificationMessage,
            details,
            origin,
            exception,
            raiseFaultEvent: true,
            deferNotification: true);

    public void QueueActivationFailure(
        string packageId,
        bool candidateReplacement,
        Exception? exception)
        => QueueFault(
            this,
            $"activation:{packageId}",
            packageId,
            viewId: null,
            candidateReplacement ? "Package App UI update failed" : PackageNotificationTitle,
            candidateReplacement
                ? CandidateActivationNotificationMessage
                : InitialActivationNotificationMessage,
            candidateReplacement
                ? "Failed to activate a candidate App package generation."
                : "Failed to activate an initial App package generation.",
            PackageFailureOrigin.AppActivation,
            exception,
            raiseFaultEvent: false,
            deferNotification: false);

    public void PublishPackageDisabled(string packageId)
    {
        AppPackageFaultNoticeIncident? incident;
        lock (_notificationSyncRoot)
        {
            _packageIncidents.TryGetValue(packageId, out incident);
        }
        incident?.Publish();
    }

    internal AppPackageFaultNoticeIncident? GetPackageDisabledIncident(string packageId)
    {
        lock (_notificationSyncRoot)
        {
            return _packageIncidents.GetValueOrDefault(packageId);
        }
    }

    private void QueueFault(
        object sender,
        string deduplicationKey,
        string packageId,
        string? viewId,
        string title,
        string message,
        string details,
        PackageFailureOrigin origin,
        Exception? exception,
        bool raiseFaultEvent,
        bool deferNotification)
    {
        lock (_notificationSyncRoot)
        {
            if (!_reportedFaults.Add(deduplicationKey))
            {
                return;
            }
        }

        var correlationId = Guid.NewGuid().ToString("N");
        AppSessionLog.WriteError(
            $"Package UI fault {correlationId} for '{packageId}'. {details}",
            exception,
            developerLogScope: DeveloperLogEntryScope.Package,
            developerLogSource: packageId);
        var identity = _resolveIdentity(packageId);
        var registration = _noticeCoordinator.Register(
            identity,
            viewId is null
                ? origin == PackageFailureOrigin.AppActivation
                    ? AppPackageFaultNoticeKind.Activation
                    : AppPackageFaultNoticeKind.Package
                : AppPackageFaultNoticeKind.View,
            viewId,
            GetNotificationTitle(identity.DisplayName, title),
            $"{message} Reference: {correlationId}.",
            correlationId,
            deferNotification);
        if (deferNotification)
        {
            lock (_notificationSyncRoot)
            {
                _packageIncidents[packageId] = registration.Incident;
            }
        }
        else if (registration.ShouldPublishImmediately)
        {
            registration.Incident.Publish();
        }
        if (raiseFaultEvent)
        {
            PublishPackageFaulted(
                sender,
                new PackageViewHostFaultEventArgs(
                    packageId,
                    message,
                    origin,
                    viewId,
                    correlationId));
        }
    }

    private static string GetNotificationTitle(string displayName, string title)
    {
        var packageName = string.IsNullOrWhiteSpace(displayName)
            ? "Package"
            : displayName.Trim();
        return title switch
        {
            ViewNotificationTitle => $"{packageName} view unavailable",
            PackageNotificationTitle => $"{packageName} App UI unavailable",
            "Package App UI update failed" => $"{packageName} App UI update failed",
            _ => title,
        };
    }

    private void PublishPackageFaulted(object sender, PackageViewHostFaultEventArgs args)
    {
        if (PackageFaulted is null)
        {
            return;
        }
        if (_uiDispatcher.CheckAccess())
        {
            InvokePackageFaultedHandlers(sender, args);
            return;
        }

        try
        {
            var publication = _uiDispatcher.InvokeAsync(
                () => InvokePackageFaultedHandlers(sender, args));
            _ = ObserveEventPublicationAsync(publication, args.PackageId);
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError(
                $"Failed to queue package fault event for '{args.PackageId}'.",
                ex);
        }
    }

    private void InvokePackageFaultedHandlers(
        object sender,
        PackageViewHostFaultEventArgs args)
    {
        var handlers = PackageFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PackageViewHostFaultEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, args);
            }
            catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                AppSessionLog.WriteError(
                    $"Package fault subscriber failed for '{args.PackageId}'.",
                    ex);
            }
        }
    }

    private async Task ObserveEventPublicationAsync(Task publication, string packageId)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError(
                $"Failed to publish package fault event for '{packageId}'.",
                ex);
        }
        catch (Exception ex)
        {
            _fatalExceptionHandler(ex);
        }
    }
}
