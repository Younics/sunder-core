using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppPackageGenerationPublication
{
    private const int MaximumStateUpdates = 128;
    private readonly Dictionary<string, StateUpdate> _latestStateUpdates = new(StringComparer.Ordinal);
    private readonly HashSet<StateUpdate> _activeStateUpdates = [];
    private readonly AsyncLocal<StateUpdateAuthority?> _stateUpdateAuthority = new();
    private TaskCompletionSource? _stateUpdatesDrained;

    internal Task RunStateUpdateAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken token)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        ArgumentNullException.ThrowIfNull(update);
        token.ThrowIfCancellationRequested();
        StateUpdate registration;
        lock (_syncRoot)
        {
            if (_state != PublicationState.Published || _dataHandoffs != 0)
                throw new InvalidOperationException("App state work cannot be accepted outside the current published activation or during settlement.");
            if (_activeStateUpdates.Count >= MaximumStateUpdates
                || !_latestStateUpdates.ContainsKey(key) && _latestStateUpdates.Count >= MaximumStateUpdates)
                throw new InvalidOperationException("The App activation's bounded state-update capacity is exhausted.");
            registration = new(CancellationTokenSource.CreateLinkedTokenSource(token, _revocation.Token));
            if (_activeStateUpdates.Count == 0) _stateUpdatesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeStateUpdates.Add(registration);
            _latestStateUpdates[key] = registration;
        }
        _ = Task.Run(() => ExecuteStateUpdateAsync(registration, update));
        return registration.Completion.Task;
    }

    private async Task ExecuteStateUpdateAsync(StateUpdate registration, Func<PackageAppStateUpdateContext, Task> update)
    {
        Exception? failure = null;
        var authority = new StateUpdateAuthority();
        var previous = _stateUpdateAuthority.Value;
        _stateUpdateAuthority.Value = authority;
        try
        {
            registration.Cancellation.Token.ThrowIfCancellationRequested();
            await update(new(registration.Settlement.Token, registration.Cancellation.Token)).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            authority.Revoke();
            _stateUpdateAuthority.Value = previous;
        }
        try { await registration.FinishAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }

        TaskCompletionSource? drained = null;
        lock (_syncRoot)
        {
            if (failure is null) registration.Completion.TrySetResult();
            else registration.Completion.TrySetException(failure);
            _activeStateUpdates.Remove(registration);
            if (_activeStateUpdates.Count == 0) { drained = _stateUpdatesDrained; _stateUpdatesDrained = null; }
        }
        drained?.TrySetResult();
    }

    private sealed class StateUpdate(CancellationTokenSource cancellation)
    {
        private readonly object _gate = new();
        private Task? _settlementRequested;
        private bool _finished;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal CancellationTokenSource Settlement { get; } = new();
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void RequestSettlement()
        {
            lock (_gate)
                if (!_finished) _settlementRequested ??= Settlement.CancelAsync();
        }
        internal async Task FinishAsync()
        {
            Task signal;
            lock (_gate) { _finished = true; signal = _settlementRequested ?? Task.CompletedTask; }
            try { await signal.ConfigureAwait(false); }
            finally { Settlement.Dispose(); Cancellation.Dispose(); }
        }
    }

    private sealed class StateUpdateAuthority
    {
        private int _active = 1;
        internal bool IsActive => Volatile.Read(ref _active) != 0;
        internal void Revoke() => Interlocked.Exchange(ref _active, 0);
    }
}
