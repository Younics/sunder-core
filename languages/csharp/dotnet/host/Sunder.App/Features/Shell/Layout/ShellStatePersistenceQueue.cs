using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Features.Shell.Layout;

internal sealed class ShellStatePersistenceQueue : IDisposable
{
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _liveState;
    private readonly Func<CancellationToken, Task> _waitToSave;
    private readonly OwnedTaskObserver _tasks = new(nameof(ShellStatePersistenceQueue));
    private CancellationTokenSource? _pendingSaveCts;
    private bool _disposed;

    public ShellStatePersistenceQueue(
        ShellStateService shellStateService,
        ShellState liveState,
        TimeSpan saveDelay)
        : this(shellStateService, liveState, cancellationToken => Task.Delay(saveDelay, cancellationToken))
    {
    }

    internal ShellStatePersistenceQueue(
        ShellStateService shellStateService,
        ShellState liveState,
        Func<CancellationToken, Task> waitToSave)
    {
        _shellStateService = shellStateService;
        _liveState = liveState;
        _waitToSave = waitToSave;
    }

    public Task QueueSave()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        CancelPendingSave();
        var cancellationTokenSource = new CancellationTokenSource();
        _pendingSaveCts = cancellationTokenSource;
        var saveTask = SaveAfterDelayAsync(cancellationTokenSource);
        _tasks.Observe(saveTask, "persisting shell state");
        return saveTask;
    }

    public void SaveImmediately()
    {
        CancelPendingSave();
        var snapshot = ShellStateSnapshotFactory.Clone(_liveState);
        var revision = _shellStateService.SaveWithRevision(snapshot);
        _liveState.Revision = Math.Max(_liveState.Revision, revision);
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPendingSave();
        _tasks.Dispose();
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await _waitToSave(cancellationTokenSource.Token);
            var snapshot = ShellStateSnapshotFactory.Clone(_liveState);
            var revision = await _shellStateService.SaveWithRevisionAsync(snapshot, cancellationTokenSource.Token);
            _liveState.Revision = Math.Max(_liveState.Revision, revision);
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
