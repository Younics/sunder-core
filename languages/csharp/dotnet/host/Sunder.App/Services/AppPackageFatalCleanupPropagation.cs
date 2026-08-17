using System.Runtime.ExceptionServices;
using Avalonia.Threading;

namespace Sunder.App.Services;

internal static class AppPackageFatalCleanupPropagation
{
    public static void PostToDispatcher(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var dispatchInfo = ExceptionDispatchInfo.Capture(exception);
        Dispatcher.UIThread.Post(
            dispatchInfo.Throw,
            DispatcherPriority.Send);
    }
}
