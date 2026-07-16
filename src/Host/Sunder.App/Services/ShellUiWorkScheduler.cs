using Avalonia.Threading;

namespace Sunder.App.Services;

internal sealed class ShellUiWorkScheduler
{
    public Task RunBelowInputPriorityAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return work(cancellationToken);
                },
                DispatcherPriority.Background,
                cancellationToken)
            .GetTask()
            .Unwrap();
    }
}
