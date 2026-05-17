using Avalonia;
using Avalonia.Threading;
using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageFaultNotifier(PackageRuntimeFaultReporter? faultReporter)
{
    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted;

    public async Task NotifyPackageDisabledAsync(
        object sender,
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception)
    {
        AppSessionLog.WriteError($"Disabled package '{packageId}' for the current app session. {message}", exception);
        faultReporter?.ReportPackageFault(packageId, origin, message);

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
}
