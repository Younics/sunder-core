using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Features.Shell.Layout;

internal sealed class ShellStatePersistenceQueue(
    ShellStateService shellStateService,
    Func<ShellState> createSnapshot,
    TimeSpan saveDelay) : IDisposable
{
    private CancellationTokenSource? _pendingSaveCts;
    private bool _disposed;

    public void QueueSave()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingSave();
        var cancellationTokenSource = new CancellationTokenSource();
        _pendingSaveCts = cancellationTokenSource;
        var snapshot = createSnapshot();
        _ = SaveAfterDelayAsync(snapshot, cancellationTokenSource);
    }

    public void SaveImmediately()
    {
        CancelPendingSave();
        shellStateService.Save(createSnapshot());
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPendingSave();
    }

    private async Task SaveAfterDelayAsync(ShellState snapshot, CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await Task.Delay(saveDelay, cancellationTokenSource.Token);
            await shellStateService.SaveAsync(snapshot, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to persist shell state.", ex);
        }
        finally
        {
            if (ReferenceEquals(_pendingSaveCts, cancellationTokenSource))
            {
                _pendingSaveCts = null;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private void CancelPendingSave()
    {
        var cancellationTokenSource = _pendingSaveCts;
        if (cancellationTokenSource is null)
        {
            return;
        }

        _pendingSaveCts = null;
        cancellationTokenSource.Cancel();
    }
}
