using System.Collections.Concurrent;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcCallScopeRegistry
{
    internal enum Outcome
    {
        Success,
        CallerLimitReached,
        AllocationFailed,
        Unavailable,
        DeadlineExceeded,
        NotFound,
        CallerMismatch,
    }

    private readonly ConcurrentDictionary<string, RuntimeRpcCallerScopeState> _scopes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeRpcCallerScopeState> _closing = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _maxScopesPerCaller;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _hostStopping;
    private readonly Action<RuntimeRpcContentAuthority>? _discardContentAuthority;

    public RuntimeRpcCallScopeRegistry(
        int maxScopesPerCaller,
        TimeProvider timeProvider,
        CancellationToken hostStopping,
        Action<RuntimeRpcContentAuthority>? discardContentAuthority)
    {
        _maxScopesPerCaller = maxScopesPerCaller;
        _timeProvider = timeProvider;
        _hostStopping = hostStopping;
        _discardContentAuthority = discardContentAuthority;
    }

    public Outcome Register(
        IRuntimeRpcCallerActivation caller,
        long sessionGeneration,
        object sessionGenerationIdentity,
        DateTimeOffset deadlineUtc,
        RuntimeRpcCallFrame? parentFrame,
        bool requirePermission,
        CancellationToken sessionRetirement,
        CancellationToken generationRetirement,
        TimeSpan deadlineDelay,
        out RuntimeRpcCallerScopeState? registeredScope)
    {
        registeredScope = null;
        var scope = new RuntimeRpcCallerScopeState(
            "rpc-scope-" + Guid.NewGuid().ToString("N"),
            caller,
            sessionGeneration,
            sessionGenerationIdentity,
            deadlineUtc,
            parentFrame,
            requirePermission,
            sessionRetirement,
            generationRetirement,
            _hostStopping);
        lock (_gate)
        {
            if (_scopes.Values.Count(item => ReferenceEquals(item.Caller, caller)) >= _maxScopesPerCaller)
            {
                _ = TeardownAsync(scope, discardBeforeRevoke: false);
                return Outcome.CallerLimitReached;
            }
            if (!_scopes.TryAdd(scope.Id, scope))
            {
                _ = TeardownAsync(scope, discardBeforeRevoke: false);
                return Outcome.AllocationFailed;
            }
        }
        try
        {
            scope.RevocationToken.Register(static stateValue =>
            {
                var (registry, registeredScope) =
                    ((RuntimeRpcCallScopeRegistry, RuntimeRpcCallerScopeState))stateValue!;
                _ = registry.RetireAsync(registeredScope);
            }, (this, scope));
            scope.ArmDeadline(_timeProvider, deadlineDelay, Expire);
        }
        catch
        {
            Expire(scope).GetAwaiter().GetResult();
            throw;
        }

        Outcome failedOutcome;
        lock (_gate)
        {
            var deadlineElapsed = _timeProvider.GetUtcNow() >= scope.DeadlineUtc;
            if (!deadlineElapsed
                && !scope.IsRevoked
                && _scopes.TryGetValue(scope.Id, out var publishedScope)
                && ReferenceEquals(publishedScope, scope))
            {
                registeredScope = scope;
                return Outcome.Success;
            }
            failedOutcome = deadlineElapsed ? Outcome.DeadlineExceeded : Outcome.Unavailable;
        }

        var teardown = failedOutcome == Outcome.DeadlineExceeded
            ? Expire(scope)
            : RetireAsync(scope);
        teardown.GetAwaiter().GetResult();
        return failedOutcome;
    }

    public Outcome Get(
        IRuntimeRpcCallerActivation caller,
        string callScopeId,
        out RuntimeRpcCallerScopeState? scope)
    {
        if (string.IsNullOrWhiteSpace(callScopeId)
            || !_scopes.TryGetValue(callScopeId, out var registeredScope)
            || !ReferenceEquals(registeredScope.Caller, caller)
            || registeredScope.IsRevoked)
        {
            scope = null;
            return Outcome.Unavailable;
        }
        if (_timeProvider.GetUtcNow() >= registeredScope.DeadlineUtc)
        {
            _ = Expire(registeredScope);
            scope = null;
            return Outcome.DeadlineExceeded;
        }
        scope = registeredScope;
        return Outcome.Success;
    }

    public Outcome Validate(
        RuntimeRpcCallerScopeState scope,
        IRuntimeRpcCallerActivation caller,
        long sessionGeneration,
        object sessionGenerationIdentity)
    {
        if (_timeProvider.GetUtcNow() >= scope.DeadlineUtc)
        {
            _ = Expire(scope);
            return Outcome.DeadlineExceeded;
        }
        if (scope.IsRevoked
            || !ReferenceEquals(scope.Caller, caller)
            || !ReferenceEquals(scope.SessionGenerationIdentity, sessionGenerationIdentity)
            || scope.SessionGeneration != sessionGeneration)
        {
            return Outcome.Unavailable;
        }
        return Outcome.Success;
    }

    public ValueTask<Outcome> CloseAsync(
        IRuntimeRpcCallerActivation caller,
        string callScopeId)
    {
        if (string.IsNullOrWhiteSpace(callScopeId)
            || !TryGetForCleanup(callScopeId, out var scope))
        {
            return ValueTask.FromResult(Outcome.NotFound);
        }
        if (!ReferenceEquals(scope.Caller, caller))
        {
            return ValueTask.FromResult(Outcome.CallerMismatch);
        }
        return CloseOwnedAsync(scope);
    }

    // Session ids are unguessable, Host-issued owner credentials. Revocation of
    // invocation authority must not revoke the ability to release that exact
    // owner's resources. This path grants no discovery, content or call rights.
    public ValueTask<Outcome> CloseAppAsync(string sessionId, string callScopeId)
    {
        if (string.IsNullOrWhiteSpace(callScopeId) || !TryGetForCleanup(callScopeId, out var scope))
            return ValueTask.FromResult(Outcome.NotFound);
        if (scope.Caller is not RuntimeRpcAppCallerSession app || app.SessionId != sessionId)
            return ValueTask.FromResult(Outcome.CallerMismatch);
        return CloseOwnedAsync(scope);
    }

    private bool TryGetForCleanup(string id, out RuntimeRpcCallerScopeState scope)
    {
        lock (_gate) return _scopes.TryGetValue(id, out scope!) || _closing.TryGetValue(id, out scope!);
    }

    public async ValueTask CloseAsync(RuntimeRpcCallerScopeState scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await CloseCoreAsync(scope).ConfigureAwait(false);
    }

    private async ValueTask<Outcome> CloseOwnedAsync(RuntimeRpcCallerScopeState scope)
    {
        await CloseCoreAsync(scope).ConfigureAwait(false);
        return Outcome.Success;
    }

    private async ValueTask CloseCoreAsync(RuntimeRpcCallerScopeState scope)
    {
        await TeardownAsync(scope, discardBeforeRevoke: false).ConfigureAwait(false);
    }

    private Task RetireAsync(RuntimeRpcCallerScopeState scope)
        => TeardownAsync(scope, discardBeforeRevoke: true);

    private Task Expire(RuntimeRpcCallerScopeState scope)
        => TeardownAsync(scope, discardBeforeRevoke: false);

    private async Task TeardownAsync(
        RuntimeRpcCallerScopeState scope,
        bool discardBeforeRevoke)
    {
        bool ownsTeardown;
        lock (_gate)
        {
            ownsTeardown = scope.TryClaimTeardown();
            if (ownsTeardown)
            {
                _closing.TryAdd(scope.Id, scope);
                _scopes.TryRemove(new KeyValuePair<string, RuntimeRpcCallerScopeState>(scope.Id, scope));
            }
        }
        if (!ownsTeardown)
        {
            await scope.TeardownCompletion.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            if (discardBeforeRevoke)
            {
                try { _discardContentAuthority?.Invoke(scope.ContentAuthority); }
                finally { await scope.Revoke().ConfigureAwait(false); }
                return;
            }
            var revocation = scope.Revoke();
            try { _discardContentAuthority?.Invoke(scope.ContentAuthority); }
            finally { await revocation.ConfigureAwait(false); }
        }
        catch (Exception error)
        {
            failure = error;
            (scope.Caller as RuntimeRpcAppCallerSession)?.Authority?.RecordCleanupFailure(error);
            throw;
        }
        finally
        {
            try { scope.CompleteTeardown(failure); }
            finally { _closing.TryRemove(new KeyValuePair<string, RuntimeRpcCallerScopeState>(scope.Id, scope)); }
        }
    }
}

internal sealed class RuntimeRpcCallerScopeState
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _revocation;
    private readonly CancellationToken _revocationToken;
    private readonly TaskCompletionSource _revocationCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _teardownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _deadlineTimer;
    private int _revoked;
    private int _teardownClaimed;
    private IDisposable? _activationLifetime;

    public RuntimeRpcCallerScopeState(
        string id,
        IRuntimeRpcCallerActivation caller,
        long sessionGeneration,
        object sessionGenerationIdentity,
        DateTimeOffset deadlineUtc,
        RuntimeRpcCallFrame? parentFrame,
        bool requirePermission,
        CancellationToken sessionRetirement,
        CancellationToken generationRetirement,
        CancellationToken hostStopping)
    {
        Id = id;
        Caller = caller;
        SessionGeneration = sessionGeneration;
        SessionGenerationIdentity = sessionGenerationIdentity;
        DeadlineUtc = deadlineUtc;
        ParentFrame = parentFrame;
        RequirePermission = requirePermission;
        ContentAuthority = new RuntimeRpcContentAuthority(id);
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(
            [
                caller.RetirementToken,
                sessionRetirement,
                generationRetirement,
                hostStopping,
            ]);
        _revocationToken = _revocation.Token;
        try { _activationLifetime = (caller as RuntimeRpcAppCallerSession)?.Authority?.Acquire(); }
        catch { _revocation.Dispose(); throw; }
    }

    public string Id { get; }
    public IRuntimeRpcCallerActivation Caller { get; }
    public long SessionGeneration { get; }
    public object SessionGenerationIdentity { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public RuntimeRpcCallFrame? ParentFrame { get; }
    public bool RequirePermission { get; }
    public RuntimeRpcContentAuthority ContentAuthority { get; }
    // Acquisition may already hold this scope when concurrent teardown disposes
    // its source. Keep the exact cancelled token, never a replacement authority.
    public CancellationToken RevocationToken => _revocationToken;
    public Task RevocationCompletion => _revocationCompleted.Task;
    public Task TeardownCompletion => _teardownCompleted.Task;
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0 || _revocation.IsCancellationRequested;

    public bool TryClaimTeardown()
        => Interlocked.CompareExchange(ref _teardownClaimed, 1, 0) == 0;

    internal void CompleteTeardown(Exception? failure)
    {
        Interlocked.Exchange(ref _activationLifetime, null)?.Dispose();
        if (failure is null) _teardownCompleted.TrySetResult();
        else _teardownCompleted.TrySetException(failure);
    }

    public void ArmDeadline(
        TimeProvider timeProvider,
        TimeSpan dueTime,
        Func<RuntimeRpcCallerScopeState, Task> expire)
    {
        var timer = timeProvider.CreateTimer(
            static state =>
            {
                var (scope, expireScope) =
                    ((RuntimeRpcCallerScopeState, Func<RuntimeRpcCallerScopeState, Task>))state!;
                _ = expireScope(scope);
            },
            (this, expire),
            dueTime <= TimeSpan.Zero ? TimeSpan.Zero : dueTime,
            Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            if (IsRevoked)
            {
                timer.Dispose();
                return;
            }
            _deadlineTimer = timer;
        }
    }

    public Task Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return _revocationCompleted.Task;
        ContentAuthority.Revoke();
        ITimer? timer;
        lock (_gate)
        {
            timer = _deadlineTimer;
            _deadlineTimer = null;
        }
        timer?.Dispose();
        var callbacks = RuntimeCancellation.Signal(_revocation);
        _ = CompleteRevocationAsync(callbacks);
        return _revocationCompleted.Task;
    }

    private async Task CompleteRevocationAsync(Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
            _revocationCompleted.TrySetResult();
        }
        catch (Exception error) { _revocationCompleted.TrySetException(error); }
        finally { _revocation.Dispose(); }
    }
}
