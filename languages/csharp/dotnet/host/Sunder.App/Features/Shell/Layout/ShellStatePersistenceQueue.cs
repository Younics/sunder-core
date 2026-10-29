using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Features.Shell.Layout;

internal sealed class ShellStatePersistenceQueue : IDisposable
{
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _liveState;
    private readonly Func<CancellationToken, Task> _waitToSave;
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

        return _shellStateService.QueueSave(_liveState, _waitToSave);
    }

    public void SaveImmediately()
    {
        _shellStateService.Flush(_liveState);
    }

    public Task FlushAsync() => _shellStateService.FlushAsync(_liveState);

    public void Dispose() => _disposed = true;
}
