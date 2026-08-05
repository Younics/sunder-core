namespace Sunder.App.Views;

internal sealed class WizardDialogLaunchCoordinator
{
    private const int Inactive = 0;
    private const int Preparing = 1;
    private const int ShowingDialog = 2;
    private const int Canceling = 3;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _launchCancellation;
    private int _phase;

    internal bool IsActive => Volatile.Read(ref _phase) != Inactive;

    internal bool HandleCloseRequest()
    {
        lock (_syncRoot)
        {
            if (_phase == Preparing)
            {
                Volatile.Write(ref _phase, Canceling);
                try
                {
                    _launchCancellation?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException)
                {
                }
            }
            else if (_phase == ShowingDialog)
            {
                return true;
            }
        }

        return false;
    }

    internal async Task RunAsync(
        Func<Action> suppressOwnerInput,
        Func<Task> deferLaunch,
        Func<bool> canShowDialog,
        Func<Task<Func<Task>?>> showDialog,
        Func<CancellationToken, Task<bool>>? prepare = null,
        CancellationToken cancellationToken = default)
    {
        var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_syncRoot)
        {
            if (_phase != Inactive)
            {
                launchCancellation.Dispose();
                return;
            }

            _launchCancellation = launchCancellation;
            Volatile.Write(ref _phase, Preparing);
        }

        Func<Task>? followUp = null;
        try
        {
            if (!canShowDialog())
            {
                return;
            }

            var restoreOwnerInput = suppressOwnerInput();
            try
            {
                await deferLaunch();
                launchCancellation.Token.ThrowIfCancellationRequested();
                if (!canShowDialog())
                {
                    return;
                }

                if (prepare is not null && !await prepare(launchCancellation.Token))
                {
                    return;
                }

                if (canShowDialog())
                {
                    lock (_syncRoot)
                    {
                        if (_phase != Preparing
                            || !ReferenceEquals(_launchCancellation, launchCancellation)
                            || launchCancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        Volatile.Write(ref _phase, ShowingDialog);
                    }
                    launchCancellation.Token.ThrowIfCancellationRequested();
                    followUp = await showDialog();
                }
            }
            finally
            {
                restoreOwnerInput();
            }
        }
        catch (OperationCanceledException) when (launchCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_launchCancellation, launchCancellation))
                {
                    _launchCancellation = null;
                    Volatile.Write(ref _phase, Inactive);
                }
            }
            launchCancellation.Dispose();
        }

        if (followUp is not null)
        {
            await followUp();
        }
    }
}
