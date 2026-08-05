using Avalonia;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed class AppPackageFaultNotifier(
    IPackageNotificationService? notificationService,
    IUiDispatcher? uiDispatcher = null)
{
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
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
        await PublishPackageDisabledNotificationAsync(packageId, message).ConfigureAwait(false);

        var args = new PackageViewHostFaultEventArgs(packageId, message, origin);
        if (_uiDispatcher.CheckAccess() || Application.Current is null)
        {
            PackageFaulted?.Invoke(sender, args);
            return;
        }

        await _uiDispatcher.InvokeAsync(() => PackageFaulted?.Invoke(sender, args));
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
