namespace Sunder.App.Services;

internal sealed class AppPackageActivationRetiredException(CancellationToken token)
    : OperationCanceledException("The App package candidate was superseded by activation retirement.", token);

internal sealed partial class AppPackageGenerationPublication
{
    private readonly object _syncRoot = new();
    private readonly List<Action> _bufferedActions = [];
    private readonly AsyncLocal<RuntimePreparationAuthority?> _runtimePreparation = new();
    private readonly CancellationTokenSource _revocation = new();
    private PublicationState _state;
    private int _dataMutations;
    private int _dataHandoffs;
    private TaskCompletionSource? _dataDrained;
    private bool _dataSettlementFailed;

    internal async Task RunDataMutationAsync(Func<Task> mutation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (_state != PublicationState.Published || _dataHandoffs != 0 && _stateUpdateAuthority.Value is not { IsActive: true })
                throw new InvalidOperationException("Package data mutation requires an active App activation outside a state handoff.");
            if (_dataMutations++ == 0) _dataDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { await mutation().ConfigureAwait(false); }
        catch
        {
            // A failed/canceled transport is not proof that the Runtime never
            // committed the write. Do not let replacement state race that write.
            lock (_syncRoot) _dataSettlementFailed = true;
            throw;
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_syncRoot)
            {
                if (--_dataMutations == 0) { drained = _dataDrained; _dataDrained = null; }
            }
            drained?.TrySetResult();
        }
    }

    // Closing admission and taking the drain snapshot are one atomic operation.
    // An aborted candidate releases only this fence; it never republishes an
    // independently revoked activation or grants disposal-time mutation rights.
    internal DataHandoff BeginDataHandoff()
    {
        StateUpdate[] updates;
        DataHandoff handoff;
        lock (_syncRoot)
        {
            _dataHandoffs++;
            updates = _activeStateUpdates.ToArray();
            handoff = new(this, WaitForDataSettlementAsync(_stateUpdatesDrained?.Task ?? Task.CompletedTask));
        }
        // CancelAsync does not invoke package callbacks inline or under the admission lock.
        foreach (var update in updates) update.RequestSettlement();
        return handoff;
    }

    // Resource drain and successful persistence are different facts. Retirement
    // must still dispose the provider after an acknowledged local request failure.
    // The false result must not acknowledge state settlement to a replacement.
    internal async Task<bool> CompleteDataMutationsAsync()
    {
        Task updates;
        lock (_syncRoot) updates = _stateUpdatesDrained?.Task ?? Task.CompletedTask;
        await WaitForLocalDataCompletionAsync(updates).ConfigureAwait(false);
        lock (_syncRoot) return !_dataSettlementFailed && _latestStateUpdates.Values.All(update => update.Completion.Task.IsCompletedSuccessfully);
    }

    internal async Task<bool> SettleAndRevokeAsync()
    {
        using var handoff = BeginDataHandoff();
        try
        {
            await handoff.Drained.WaitAsync(AppShutdownBudgets.GenerationRetirement).ConfigureAwait(false);
            handoff.Commit();
            return true;
        }
        catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
        {
            AppSessionLog.WriteError("Accepted App state work did not confirm settlement before retirement.", error);
            if (!handoff.Drained.IsCompleted)
                AppCleanupQuarantine.Retain(handoff.Drained, "settling accepted App state work before retirement");
            return false;
        }
        finally { Revoke(); }
    }

    private async Task WaitForDataSettlementAsync(Task updates)
    {
        await WaitForLocalDataCompletionAsync(updates).ConfigureAwait(false);
        lock (_syncRoot)
            if (_dataSettlementFailed || _latestStateUpdates.Values.Any(update => !update.Completion.Task.IsCompletedSuccessfully))
                throw DataSettlementFailure();
    }

    private async Task WaitForLocalDataCompletionAsync(Task updates)
    {
        // Accepted callbacks can issue storage mutations while normal admission is
        // closed. Take the storage-drain snapshot only after their authority ends.
        await updates.ConfigureAwait(false);
        Task drain;
        lock (_syncRoot) drain = _dataDrained?.Task ?? Task.CompletedTask;
        await drain.ConfigureAwait(false);
    }

    private static InvalidOperationException DataSettlementFailure()
        => new("An App package data mutation has no successful settlement acknowledgement; replacement or retirement cannot be proven.");

    internal sealed class DataHandoff(AppPackageGenerationPublication owner, Task drained) : IDisposable
    {
        private AppPackageGenerationPublication? _owner = owner;
        internal Task Drained { get; } = drained;
        internal void Commit()
        {
            if (!Drained.IsCompletedSuccessfully)
                throw new InvalidOperationException("An App data handoff cannot commit without successful write settlement.");
            var current = Interlocked.Exchange(ref _owner, null)
                ?? throw new InvalidOperationException("The App data handoff has already been released.");
            // Older queued presentations may still own resource references. Those
            // references must not keep this exact predecessor's authority alive.
            try { current.Revoke(); }
            finally { Release(current); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } current) return;
            Release(current);
        }
        private static void Release(AppPackageGenerationPublication current)
        {
            lock (current._syncRoot) current._dataHandoffs--;
        }
    }

    internal CancellationToken RevocationToken => _revocation.Token;

    public bool IsPublished
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Published;
            }
        }
    }

    public bool IsRevoked
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Revoked;
            }
        }
    }

    public bool IsRuntimeAvailable
    {
        get
        {
            lock (_syncRoot)
            {
                return _state == PublicationState.Published
                       || _state == PublicationState.Pending
                       && _runtimePreparation.Value is { IsActive: true };
            }
        }
    }

    public IDisposable BeginRuntimePreparation()
    {
        lock (_syncRoot)
        {
            if (_state == PublicationState.Revoked)
                throw new AppPackageActivationRetiredException(_revocation.Token);
            if (_state != PublicationState.Pending)
            {
                throw new InvalidOperationException(
                    "Runtime preparation authority is only available to a pending App generation.");
            }
        }

        var previous = _runtimePreparation.Value;
        var authority = new RuntimePreparationAuthority();
        _runtimePreparation.Value = authority;
        return new RuntimePreparationScope(this, authority, previous);
    }

    public void Buffer(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_syncRoot)
        {
            if (_state == PublicationState.Pending)
            {
                _bufferedActions.Add(publish);
                return;
            }

            if (_state == PublicationState.Revoked)
            {
                return;
            }
        }

        publish();
    }

    public void Publish()
    {
        Action[] actions;
        lock (_syncRoot)
        {
            if (_state != PublicationState.Pending)
            {
                return;
            }

            _state = PublicationState.Published;
            actions = _bufferedActions.ToArray();
            _bufferedActions.Clear();
        }

        foreach (var action in actions)
        {
            InvokeBufferedAction(action);
        }
    }

    public void Revoke()
    {
        lock (_syncRoot)
        {
            if (_state == PublicationState.Revoked)
            {
                return;
            }

            _state = PublicationState.Revoked;
            _bufferedActions.Clear();
        }

        try
        {
            _revocation.Cancel();
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError(
                "Failed to cancel work owned by a retired App package generation.",
                exception);
        }
    }

    public void RequirePublished(string capability)
    {
        if (!IsPublished)
        {
            throw new InvalidOperationException(
                $"Package capability '{capability}' is unavailable until its App generation has been published.");
        }
    }

    public CancellationToken RequireRuntimeAccess(string capability)
    {
        lock (_syncRoot)
        {
            if (_state == PublicationState.Published
                || _state == PublicationState.Pending
                && _runtimePreparation.Value is { IsActive: true })
            {
                return _revocation.Token;
            }
        }

        throw new InvalidOperationException(
            $"Package capability '{capability}' is unavailable outside its current App generation or candidate preparation.");
    }

    private void EndRuntimePreparation(
        RuntimePreparationAuthority authority,
        RuntimePreparationAuthority? previous)
    {
        authority.Revoke();
        if (ReferenceEquals(_runtimePreparation.Value, authority))
        {
            _runtimePreparation.Value = previous;
        }
    }

    private static void InvokeBufferedAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError("Failed to publish a buffered App package side effect.", exception);
        }
    }

    private enum PublicationState
    {
        Pending,
        Published,
        Revoked,
    }

    private sealed class RuntimePreparationAuthority
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Revoke() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class RuntimePreparationScope(
        AppPackageGenerationPublication owner,
        RuntimePreparationAuthority authority,
        RuntimePreparationAuthority? previous) : IDisposable
    {
        private AppPackageGenerationPublication? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndRuntimePreparation(authority, previous);
    }
}
