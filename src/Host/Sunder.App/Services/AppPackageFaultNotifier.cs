using Avalonia;
using Avalonia.Threading;
using Sunder.Protocol;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed class AppPackageFaultNotifier(
    PackageRuntimeFaultReporter? faultReporter,
    IPackageNotificationService? notificationService)
{
    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted;

    public async Task NotifyPackageDisabledAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        AppSessionLog.WriteError(
            $"Disabled package '{packageId}' for the current app session. {message}",
            exception,
            developerLogScope: DeveloperLogEntryScope.Package,
            developerLogSource: packageId);
        faultReporter?.ReportPackageFault(packageId, origin, message);
        await PublishPackageDisabledNotificationAsync(packageId, message).ConfigureAwait(false);

        var args = new PackageViewHostFaultEventArgs(packageId, message, origin);
        if (Dispatcher.UIThread.CheckAccess() || Application.Current is null)
        {
            PackageFaulted?.Invoke(sender, args);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => PackageFaulted?.Invoke(sender, args),
            DispatcherPriority.Normal);
    }

    private async ValueTask PublishPackageDisabledNotificationAsync(string packageId, string message)
    {
        if (notificationService is null)
        {
            return;
        }

        try
        {
            await notificationService.PublishAsync(new PackageNotificationRequest(
                "Package disabled",
                $"{packageId} was disabled for this app session: {message}",
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Error)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed to publish package fault notification for '{packageId}'.", ex);
        }
    }
}
