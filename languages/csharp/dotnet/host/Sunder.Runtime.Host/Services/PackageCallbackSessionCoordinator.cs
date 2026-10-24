using System.Text;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using ProtocolCallbackState = Sunder.Runtime.Contracts.PackageCallbackSessionState;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageCallbackSessionCoordinator
{
    private readonly PackageSessionState _sessionState;
    private readonly RuntimeAuthPolicyOptions _policy;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _hostStopping;
    private readonly CancellationTokenRegistration _hostStoppingRegistration;
    private readonly RuntimeRpcCatalog? _rpcCatalog;
    private readonly PackageCallbackSessionStore _sessions = new();
    private readonly object _transitionSync = new();
    private readonly object _cleanupSync = new();
    private readonly HashSet<Task> _cleanupTasks = [];
    private int _stopping;

    public PackageCallbackSessionCoordinator(
        PackageSessionState sessionState,
        RuntimeAuthPolicyOptions? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken hostStopping = default,
        RuntimeRpcCatalog? rpcCatalog = null)
    {
        _sessionState = sessionState;
        _policy = policy ?? new RuntimeAuthPolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hostStopping = hostStopping;
        _rpcCatalog = rpcCatalog;
        _hostStoppingRegistration = hostStopping.UnsafeRegister(
            static state => ((PackageCallbackSessionCoordinator)state!).BeginHostStopping(),
            this);
    }

    public async Task<PackageCallbackSessionResponse?> StartAsync(
        PackageSessionLease lease,
        string packageId,
        string callbackHandlerId,
        IReadOnlyDictionary<string, string>? parameters,
        PackageCallbackServer callbackServer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        ValidateHandlerId(callbackHandlerId);
        var parameterSnapshot = PackageCallbackParameters.CopyAndValidate(parameters);
        SweepExpired(_timeProvider.GetUtcNow());
        var loadedPackage = _sessionState.GetLoadedPackage(lease, packageId);
        var handler = loadedPackage?.GetCallbackHandler(callbackHandlerId);
        if (handler is null) return null;
        using var activation = AcquireActivation(loadedPackage!);

        var key = new PackageCallbackStartKey(
            packageId.ToUpperInvariant(),
            loadedPackage!.RuntimeActivationId,
            callbackHandlerId.ToUpperInvariant(),
            BuildParametersKey(parameterSnapshot));
        var reservation = _sessions.BeginStart(
            key,
            _policy.MaxPackageCallbackSessions,
            () => CreateStartingSession(loadedPackage.RuntimeActivationId, packageId, key, handler, callbackServer));
        using var linked = CreateCancellation(lease, activation, cancellationToken);
        if (!reservation.IsOwner)
        {
            return await reservation.Session.StartCompletion.Task.WaitAsync(linked.Token);
        }

        var session = reservation.Session;
        var packageStartInvoked = false;
        try
        {
            callbackServer.EnsureStarted();
            packageStartInvoked = true;
            var result = await InvokeStartAsync(
                session,
                callbackHandlerId,
                parameterSnapshot,
                linked.Token);
            if (result is null)
            {
                await FinishRejectedStartAsync(session, PackageCallbackLifecycleState.Failed, packageStartInvoked);
                session.StartCompletion.TrySetResult(null);
                return null;
            }
            ValidateStartResult(packageId, session.SessionId, result);

            var status = new PackageCallbackSessionResponse(
                packageId,
                callbackHandlerId,
                session.SessionId,
                ProtocolCallbackState.Pending,
                BoundMessage(result.Message),
                result.LaunchUrl,
                session.ExpiresAtUtc);
            linked.Token.ThrowIfCancellationRequested();
            var published = activation is null
                ? _sessionState.ExecuteIfCurrent(lease, () => _sessions.TryPublishPending(session, status))
                : _sessionState.ExecuteIfActivationCurrent(loadedPackage, () => _sessions.TryPublishPending(session, status));
            if (!published)
            {
                await FinishRejectedStartAsync(session, PackageCallbackLifecycleState.Cancelled, packageStartInvoked);
                linked.Token.ThrowIfCancellationRequested();
                session.StartCompletion.TrySetResult(null);
                return null;
            }

            session.StartCompletion.TrySetResult(status);
            return status;
        }
        catch (OperationCanceledException exception)
        {
            await FinishRejectedStartAsync(session, PackageCallbackLifecycleState.Cancelled, packageStartInvoked);
            session.StartCompletion.TrySetCanceled(exception.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            await FinishRejectedStartAsync(session, PackageCallbackLifecycleState.Failed, packageStartInvoked);
            session.StartCompletion.TrySetException(exception);
            throw;
        }
    }

    private ActivePackageCallbackSession CreateStartingSession(
        Guid activationId,
        string packageId,
        PackageCallbackStartKey key,
        IPackageCallbackHandler handler,
        PackageCallbackServer callbackServer)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        return new ActivePackageCallbackSession(
            packageId,
            activationId,
            key,
            sessionId,
            handler,
            callbackServer,
            _timeProvider.GetUtcNow() + _policy.SessionLifetime,
            new CancellationTokenSource(),
            (query, token) => CompleteWithCurrentLeaseAsync(sessionId, query, token));
    }

    private async Task<PackageCallbackStartResult?> InvokeStartAsync(
        ActivePackageCallbackSession session,
        string callbackHandlerId,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_policy.PackageCallbackStartTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.PendingCancellation.Token,
            timeout.Token);
        var callbackUri = string.Equals(
            callbackHandlerId,
            PackageCallbackHandlerIds.Authentication,
            StringComparison.OrdinalIgnoreCase)
            ? session.CallbackServer.GetAuthenticationCallbackUri()
            : session.CallbackServer.GetCallbackUri(session.SessionId);
        var context = new PackageCallbackStartContext(
            session.SessionId,
            callbackUri,
            callbackHandlerId,
            parameters);
        var packageTask = Task.Run(() => session.Handler.StartCallbackAsync(context, linked.Token));
        try
        {
            return await packageTask.WaitAsync(_policy.PackageCallbackStartTimeout, _timeProvider, linked.Token);
        }
        catch
        {
            ObservePackageTask(packageTask);
            throw;
        }
    }

    private async Task FinishRejectedStartAsync(
        ActivePackageCallbackSession session,
        PackageCallbackLifecycleState terminalState,
        bool packageStartInvoked)
    {
        if (!_sessions.TryFinishStart(session, terminalState))
        {
            return;
        }
        if (packageStartInvoked)
        {
            await CancelAndDisposeAsync(session, PackageCallbackCancellationReason.PackageUnloaded);
        }
        else
        {
            session.PendingCancellation.Dispose();
        }
    }

    public PackageCallbackSessionResponse? GetStatus(
        PackageSessionLease lease,
        string packageId,
        string callbackSessionId)
    {
        SweepExpired(_timeProvider.GetUtcNow());
        var package = _sessionState.GetLoadedPackage(lease, packageId);
        return package is null ? null : _sessions.GetStatus(packageId, callbackSessionId, package.RuntimeActivationId);
    }

    public bool Cancel(
        PackageSessionLease lease,
        string packageId,
        string callbackSessionId)
    {
        var now = _timeProvider.GetUtcNow();
        SweepExpired(now);
        var package = _sessionState.GetLoadedPackage(lease, packageId);
        if (package is null) return false;
        lock (_transitionSync)
        {
            var cancellation = _sessions.TryCancel(
                packageId,
                callbackSessionId,
                package.RuntimeActivationId,
                now + _policy.TerminalSessionRetention);
            if (cancellation is null)
            {
                return false;
            }

            ScheduleCancellations([cancellation.Value]);
            return true;
        }
    }

    public async Task<bool> CompleteAsync(
        PackageSessionLease lease,
        string callbackSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
    {
        SweepExpired(_timeProvider.GetUtcNow());
        var packageId = _sessions.GetPackageId(callbackSessionId);
        var package = packageId is null ? null : _sessionState.GetLoadedPackage(lease, packageId);
        if (package is null) return false;
        using var activation = AcquireActivation(package);
        var session = _sessions.TryClaimCompletion(callbackSessionId, package.RuntimeActivationId);
        if (session is null) return false;
        session.PendingCancellation.Dispose();
        var completionCancellation = session.CompletionCancellation!;

        PackageCallbackCompletionResult completion;
        try
        {
            using var timeout = new CancellationTokenSource(_policy.PackageCallbackCompletionTimeout, _timeProvider);
            using var lifecycle = CreateCancellation(lease, activation, cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifecycle.Token,
                timeout.Token,
                completionCancellation.Token);
            var context = new PackageCallbackCompletionContext(callbackSessionId, queryValues);
            var packageTask = Task.Run(() => session.Handler.CompleteCallbackAsync(context, linked.Token));
            try
            {
                completion = await packageTask.WaitAsync(
                    _policy.PackageCallbackCompletionTimeout,
                    _timeProvider,
                    linked.Token);
            }
            catch
            {
                ObservePackageTask(packageTask);
                throw;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!string.Equals(completion.PackageId, session.PackageId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(completion.CallbackSessionId, callbackSessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The callback handler returned mismatched session identity.");
            }
        }
        catch (OperationCanceledException)
        {
            completion = new PackageCallbackCompletionResult(
                session.PackageId,
                callbackSessionId,
                PackageCallbackCompletionState.Cancelled,
                "The callback session was cancelled.");
        }
        catch (Exception exception)
        {
            completion = new PackageCallbackCompletionResult(
                session.PackageId,
                callbackSessionId,
                PackageCallbackCompletionState.Failed,
                exception.Message);
        }

        var finalStatus = session.Status! with
        {
            State = completion.State switch
            {
                PackageCallbackCompletionState.Completed => ProtocolCallbackState.Completed,
                PackageCallbackCompletionState.Cancelled => ProtocolCallbackState.Cancelled,
                _ => ProtocolCallbackState.Failed,
            },
            Message = BoundMessage(completion.Message),
        };
        lock (_transitionSync)
        {
            finalStatus = _sessions.Complete(
                session,
                finalStatus,
                _timeProvider.GetUtcNow() + _policy.TerminalSessionRetention);
        }
        session.CompletionWork!.TrySetResult();
        completionCancellation.Dispose();
        return finalStatus.State == ProtocolCallbackState.Completed;
    }

    public void Clear()
    {
        lock (_transitionSync)
        {
            ScheduleCancellations(_sessions.Clear());
        }
    }

    internal void Reconcile(ActivePackageSession session)
    {
        var activations = session.LoadedPackageMap.Values.Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .ToDictionary(package => package.Descriptor.PackageId, package => package.RuntimeActivationId, StringComparer.OrdinalIgnoreCase);
        lock (_transitionSync) ScheduleCancellations(_sessions.Reconcile(activations));
    }

    private RuntimeRpcActivationLease? AcquireActivation(ActiveLoadedPackage package)
    {
        if (_rpcCatalog is null) return null;
        if (!_rpcCatalog.TryGetPackage(package.Descriptor.PackageId, package.RuntimeActivationId, out var owner)
            || owner is null || !owner.ContinuesAs(package) || !owner.TryAcquireCallee(out var lease) || lease is null)
            throw new RuntimeUnavailableException("The package callback activation is retired.");
        return lease;
    }

    private CancellationTokenSource CreateCancellation(PackageSessionLease session, RuntimeRpcActivationLease? activation, CancellationToken request)
        => activation is null ? session.CreateLinkedCancellation(request, _hostStopping)
            : CancellationTokenSource.CreateLinkedTokenSource(request, _hostStopping, activation.RetirementToken);

    public void RemovePackageSessions(string packageId)
    {
        lock (_transitionSync)
        {
            ScheduleCancellations(_sessions.RemovePackage(packageId));
        }
    }

    internal void SweepExpired(DateTimeOffset now)
    {
        lock (_transitionSync)
        {
            ScheduleCancellations(_sessions.Sweep(now, _policy.TerminalSessionRetention));
        }
    }

    public async Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _hostStoppingRegistration.Dispose();
        PackageCallbackShutdownWork shutdown;
        lock (_transitionSync)
        {
            shutdown = _sessions.BeginShutdown();
            ScheduleCancellations(shutdown.Cancellations);
        }

        Task[] work;
        lock (_cleanupSync)
        {
            _cleanupTasks.RemoveWhere(static task => task.IsCompleted);
            work = _cleanupTasks.Concat(shutdown.CompletingTasks).ToArray();
        }
        if (work.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(work).WaitAsync(_policy.PackageCallbackShutdownTimeout, _timeProvider);
        }
        catch (TimeoutException)
        {
            // Package work remains isolated by its session lease and is observed when it exits.
        }
    }

    private void BeginHostStopping()
    {
        Interlocked.Exchange(ref _stopping, 1);
        lock (_transitionSync)
        {
            var shutdown = _sessions.BeginShutdown();
            ScheduleCancellations(shutdown.Cancellations);
        }
    }

    private async Task<bool> CompleteWithCurrentLeaseAsync(
        string sessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken)
    {
        using var lease = _sessionState.AcquireLease();
        return await CompleteAsync(lease, sessionId, queryValues, cancellationToken);
    }

    private void ScheduleCancellations(IReadOnlyList<PackageCallbackCancellationWork> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            if (cancellation.CancelCompletion)
            {
                try
                {
                    cancellation.Session.CompletionCancellation?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                continue;
            }

            cancellation.Session.StartCompletion.TrySetResult(null);
            try
            {
                cancellation.Session.PendingCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            TrackCleanup(CancelAndDisposeAsync(cancellation.Session, cancellation.Reason));
        }
    }

    private async Task CancelAndDisposeAsync(
        ActivePackageCallbackSession session,
        PackageCallbackCancellationReason reason)
    {
        try
        {
            try
            {
                session.PendingCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            using var timeout = new CancellationTokenSource(_policy.PackageCallbackCancellationTimeout, _timeProvider);
            var context = new PackageCallbackCancellationContext(session.SessionId, reason);
            var packageTask = Task.Run(() => session.Handler.CancelCallbackAsync(context, timeout.Token));
            try
            {
                await packageTask.WaitAsync(
                    _policy.PackageCallbackCancellationTimeout,
                    _timeProvider,
                    timeout.Token);
            }
            catch
            {
                ObservePackageTask(packageTask);
            }
        }
        catch
        {
            // Cancellation is best effort and must not block lifecycle progress.
        }
        finally
        {
            session.PendingCancellation.Dispose();
        }
    }

    private void TrackCleanup(Task cleanup)
    {
        lock (_cleanupSync)
        {
            _cleanupTasks.RemoveWhere(static task => task.IsCompleted);
            _cleanupTasks.Add(cleanup);
        }
    }

    private static void ObservePackageTask(Task task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            _ = ObservePackageTaskAsync(task);
        }
    }

    private static async Task ObservePackageTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _stopping) != 0 || _hostStopping.IsCancellationRequested)
        {
            throw new RuntimeUnavailableException("Package callback sessions are shutting down.");
        }
    }

    private static void ValidateHandlerId(string handlerId)
    {
        if (string.IsNullOrWhiteSpace(handlerId)
            || handlerId.Length > 128
            || handlerId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new RuntimeValidationException("Callback handler ids must be ASCII tokens of at most 128 characters.");
        }
    }

    private static void ValidateStartResult(string packageId, string sessionId, PackageCallbackStartResult result)
    {
        if (!string.Equals(result.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.CallbackSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The callback handler returned mismatched session identity.");
        }
        if (!Uri.TryCreate(result.LaunchUrl, UriKind.Absolute, out _)
            || result.LaunchUrl.Length > 4096)
        {
            throw new InvalidOperationException("The callback handler returned an invalid launch URI.");
        }
    }

    private static string BoundMessage(string? message)
        => string.IsNullOrWhiteSpace(message) ? string.Empty : message.Length <= 2048 ? message : message[..2048];

    private static string BuildParametersKey(IReadOnlyDictionary<string, string> parameters)
    {
        var builder = new StringBuilder();
        foreach (var pair in parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key.Length).Append(':').Append(pair.Key)
                .Append(pair.Value.Length).Append(':').Append(pair.Value);
        }
        return builder.ToString();
    }
}
