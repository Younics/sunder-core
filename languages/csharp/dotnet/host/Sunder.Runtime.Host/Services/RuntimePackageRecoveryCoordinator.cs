using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal enum RuntimePackageRecoveryValidation
{
    Stale,
    DependencyUnavailable,
    Ready,
}

internal sealed record RuntimePackageRecoveryRequest(
    object SessionIdentity,
    ActivePackageSession Session,
    string PackageId,
    Guid FailedActivationId,
    long ActivationGeneration,
    RuntimePackageSource Source,
    ActiveLoadedPackage FailedPackage,
    Task CleanupCompletion,
    PackageFailureOrigin FailureOrigin,
    Exception Failure,
    DateTimeOffset FailedAtUtc)
{
    public Func<ActiveLoadedPackage> RecoveryFactory
        => FailedPackage.RecoveryFactory
           ?? throw new InvalidOperationException(
               $"Package '{PackageId}' does not have a Runtime recovery factory.");
}

internal sealed class RuntimePackageRecoveryQueue
{
    private readonly Channel<RuntimePackageRecoveryRequest> _requests =
        Channel.CreateUnbounded<RuntimePackageRecoveryRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    public void Enqueue(RuntimePackageRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Writer.TryWrite(request);
    }

    internal ChannelReader<RuntimePackageRecoveryRequest> Reader => _requests.Reader;
}

internal sealed class RuntimePackageRecoveryCoordinator : IHostedService, IAsyncDisposable
{
    private const int MaximumRememberedRequests = 2048;
    private const int MaximumRecoveryHistories = 1024;
    private readonly RuntimePackageRecoveryQueue _queue;
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _operationGate;
    private readonly RuntimeRpcCatalog _rpcCatalog;
    private readonly RuntimeEventStreamService _events;
    private readonly RuntimePackageRecoveryPolicyOptions _policy;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimePackageRecoveryCoordinator> _logger;
    private readonly Func<string, TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<CancellationToken, Task>? _beforeRecoverySuccess;
    private readonly Func<PackageStorageActivationTransaction, Task> _rollbackAsync;
    private readonly Action? _requestHostStop;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _hostStoppingRegistration;
    private readonly object _stateGate = new();
    private readonly Dictionary<RecoveryRequestKey, Task> _activeRequests = [];
    private readonly HashSet<RecoveryRequestKey> _seenRequests = [];
    private readonly Queue<RecoveryRequestKey> _seenRequestOrder = [];
    private readonly Dictionary<RecoveryHistoryKey, Queue<DateTimeOffset>> _failureHistory = [];
    private readonly Queue<RecoveryHistoryKey> _failureHistoryOrder = [];
    private readonly List<QuarantinedRecovery> _quarantinedRecoveries = [];
    private Task _readerTask = Task.CompletedTask;
    private int _integrityFailureSignaled;
    private bool _started;
    private bool _disposed;

    public RuntimePackageRecoveryCoordinator(
        RuntimePackageRecoveryQueue queue,
        RuntimeSessionOwner sessions,
        RuntimeOperationGate operationGate,
        RuntimeRpcCatalog rpcCatalog,
        RuntimeEventStreamService events,
        RuntimePackageRecoveryPolicyOptions policy,
        RuntimeLifecyclePolicyOptions lifecyclePolicy,
        TimeProvider timeProvider,
        ILogger<RuntimePackageRecoveryCoordinator> logger,
        IHostApplicationLifetime? hostLifetime = null)
        : this(
            queue,
            sessions,
            operationGate,
            rpcCatalog,
            events,
            policy,
            lifecyclePolicy,
            timeProvider,
            logger,
            (_, delay, cancellationToken) => Task.Delay(delay, timeProvider, cancellationToken),
            hostLifetime?.ApplicationStopping ?? CancellationToken.None,
            beforeRecoverySuccess: null,
            rollbackAsync: null,
            requestHostStop: hostLifetime is null ? null : hostLifetime.StopApplication)
    {
    }

    internal RuntimePackageRecoveryCoordinator(
        RuntimePackageRecoveryQueue queue,
        RuntimeSessionOwner sessions,
        RuntimeOperationGate operationGate,
        RuntimeRpcCatalog rpcCatalog,
        RuntimeEventStreamService events,
        RuntimePackageRecoveryPolicyOptions policy,
        RuntimeLifecyclePolicyOptions lifecyclePolicy,
        TimeProvider timeProvider,
        ILogger<RuntimePackageRecoveryCoordinator> logger,
        Func<string, TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken hostStopping = default,
        Func<CancellationToken, Task>? beforeRecoverySuccess = null,
        Func<PackageStorageActivationTransaction, Task>? rollbackAsync = null,
        Action? requestHostStop = null)
    {
        _queue = queue;
        _sessions = sessions;
        _operationGate = operationGate;
        _rpcCatalog = rpcCatalog;
        _events = events;
        _policy = ValidatePolicy(policy);
        _lifecyclePolicy = lifecyclePolicy;
        _timeProvider = timeProvider;
        _logger = logger;
        _delayAsync = delayAsync;
        _beforeRecoverySuccess = beforeRecoverySuccess;
        _rollbackAsync = rollbackAsync ?? (storage => storage.RollbackAsync(_logger));
        _requestHostStop = requestHostStop;
        _hostStoppingRegistration = hostStopping.Register(static state =>
            ((CancellationTokenSource)state!).Cancel(), _shutdown);
    }

    internal int QuarantinedRecoveryCount
    {
        get
        {
            lock (_stateGate) return _quarantinedRecoveries.Count;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return Task.CompletedTask;
            }
            _started = true;
            _readerTask = RunReaderAsync(_shutdown.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        Task reader;
        lock (_stateGate)
        {
            reader = _readerTask;
        }
        await ObserveCancellationAsync(reader, cancellationToken).ConfigureAwait(false);
        Task[] recoveries;
        lock (_stateGate)
        {
            recoveries = _activeRequests.Values.ToArray();
        }
        await ObserveCancellationAsync(Task.WhenAll(recoveries), cancellationToken).ConfigureAwait(false);
        await ObserveQuarantinesDuringShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _hostStoppingRegistration.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunReaderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_policy.Enabled)
                {
                    continue;
                }
                var key = new RecoveryRequestKey(
                    request.SessionIdentity,
                    request.PackageId,
                    request.FailedActivationId);
                lock (_stateGate)
                {
                    if (!_seenRequests.Add(key))
                    {
                        continue;
                    }
                    _seenRequestOrder.Enqueue(key);
                    while (_seenRequestOrder.Count > MaximumRememberedRequests)
                    {
                        _seenRequests.Remove(_seenRequestOrder.Dequeue());
                    }
                    var failureCount = RecordFailureLocked(request, request.FailedAtUtc);
                    var recovery = Task.Run(
                        () => RecoverAsync(request, failureCount, cancellationToken),
                        CancellationToken.None);
                    _activeRequests.Add(key, recovery);
                    _ = RemoveCompletedRequestAsync(key, recovery);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverAsync(
        RuntimePackageRecoveryRequest request,
        int failureCount,
        CancellationToken cancellationToken)
    {
        if (failureCount >= _policy.SuspensionFailureCount)
        {
            await PublishSuspendedIfCurrentAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = _policy.RestartDelays[failureCount - 1];
        var initialDelay = _delayAsync(request.PackageId, delay, cancellationToken);
        try
        {
            await request.CleanupCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await initialDelay.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = ObserveAsync(initialDelay);
            _logger.LogError(
                exception,
                "Package {PackageId} cleanup could not be proven before recovery",
                request.PackageId);
            await PublishSuspendedIfCurrentAsync(
                    request,
                    CancellationToken.None,
                    $"Package '{request.PackageId}' restart was suspended because cleanup of the failed worker could not be proven.")
                .ConfigureAwait(false);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            RecoveryAttemptResult outcome;
            try
            {
                RuntimeOperationGate.Lease? operation = await _operationGate.EnterAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    outcome = await ExecuteAttemptUnderGateAsync(
                            request,
                            failureCount,
                            operation.CancellationToken)
                        .ConfigureAwait(false);
                    if (outcome.Quarantine is { } pendingQuarantine)
                    {
                        TrackQuarantinedRecovery(pendingQuarantine, operation);
                        operation = null;
                    }
                }
                finally
                {
                    if (operation is not null)
                    {
                        await operation.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (outcome.Disposition == RecoveryAttemptDisposition.Stop)
            {
                return;
            }
            if (outcome.Disposition == RecoveryAttemptDisposition.Suspend)
            {
                PublishSuspensionEvent(
                    request,
                    outcome.Generation,
                    outcome.Message
                    ?? $"Package '{request.PackageId}' restart was suspended.");
                return;
            }

            if (outcome.Disposition == RecoveryAttemptDisposition.Retry)
            {
                failureCount = outcome.FailureCount;
                try
                {
                    await _delayAsync(
                            request.PackageId,
                            _policy.RestartDelays[failureCount - 1],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }

            try
            {
                await _delayAsync(request.PackageId, _policy.DependencyRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<RecoveryAttemptResult> ExecuteAttemptUnderGateAsync(
        RuntimePackageRecoveryRequest request,
        int failureCount,
        CancellationToken operationToken)
    {
        ActiveLoadedPackage? replacement = null;
        PackageStorageActivationTransaction? storage = null;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var validation = _sessions.State.ValidatePackageRecovery(request);
            if (validation == RuntimePackageRecoveryValidation.Stale)
            {
                return RecoveryAttemptResult.Stop;
            }
            if (validation == RuntimePackageRecoveryValidation.DependencyUnavailable)
            {
                return RecoveryAttemptResult.WaitForDependency;
            }

            _events.PublishPackageRecovery(
                RuntimeEventKind.PackageRecoveryStarted,
                _sessions.Generation,
                request.PackageId,
                success: null,
                $"Restarting package '{request.PackageId}' after its Worker V1 activation failed.");
            operationToken.ThrowIfCancellationRequested();
            replacement = request.RecoveryFactory();
            ValidateReplacement(request, replacement);
            replacement = replacement with { Source = request.Source };
            storage = await PackageStorageActivationTransaction.BeginAsync(
                    [replacement],
                    operationToken)
                .ConfigureAwait(false);
            replacement = await ActivateReplacementAsync(
                    request,
                    replacement,
                    storage,
                    operationToken)
                .ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            var commit = _sessions.CommitPackageRecovery(
                request,
                replacement,
                operationToken,
                out var committedGeneration);
            if (commit != RuntimePackageRecoveryValidation.Ready)
            {
                throw commit == RuntimePackageRecoveryValidation.DependencyUnavailable
                    ? new RecoveryDependencyUnavailableException()
                    : new RecoveryAuthorityStaleException();
            }

            await storage.CompleteAsync(_logger).ConfigureAwait(false);
            storage = null;
            var committedReplacement = replacement;
            replacement = null;
            if (!request.Session.StartRecoveredRuntimeGenerationMonitoring(
                    request.PackageId,
                    committedReplacement.RuntimeActivationId,
                    request.ActivationGeneration,
                    _sessions.HandleRuntimeGenerationFault))
            {
                _logger.LogCritical(
                    "Recovered package {PackageId} was published without lifecycle monitoring",
                    request.PackageId);
                return RecoveryAttemptResult.Stop;
            }
            if (_beforeRecoverySuccess is not null)
            {
                await _beforeRecoverySuccess(operationToken).ConfigureAwait(false);
            }
            operationToken.ThrowIfCancellationRequested();
            _sessions.State.TryPublishCurrentRecoverySuccess(
                request,
                committedReplacement,
                committedGeneration,
                () => _events.PublishPackageRecovery(
                    RuntimeEventKind.PackageRecoverySucceeded,
                    committedGeneration,
                    request.PackageId,
                    success: true,
                    $"Package '{request.PackageId}' restarted successfully.") is not null);
            return RecoveryAttemptResult.Stop;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (replacement is null)
            {
                return RecoveryAttemptResult.Stop;
            }
            var rejection = await RejectReplacementUnderGateAsync(
                    request,
                    replacement,
                    storage,
                    CloseRecoveryAuthority(request, replacement),
                    "restart was suspended because shutdown interrupted replacement cleanup")
                .ConfigureAwait(false);
            return rejection.IntegrityFailure
                ? RecoveryAttemptResult.Stop
                : rejection.Safe
                ? RecoveryAttemptResult.Stop
                : RecoveryAttemptResult.Suspend(
                    _sessions.Generation,
                    rejection.Message!,
                    rejection.Quarantine);
        }
        catch (RecoveryDependencyUnavailableException)
        {
            if (replacement is null)
            {
                return RecoveryAttemptResult.WaitForDependency;
            }
            var rejection = await RejectReplacementUnderGateAsync(
                    request,
                    replacement,
                    storage,
                    CloseRecoveryAuthority(request, replacement),
                    "restart was suspended because dependency-wait cleanup could not be proven")
                .ConfigureAwait(false);
            return rejection.IntegrityFailure
                ? RecoveryAttemptResult.Stop
                : rejection.Safe
                ? RecoveryAttemptResult.WaitForDependency
                : RecoveryAttemptResult.Suspend(
                    _sessions.Generation,
                    rejection.Message!,
                    rejection.Quarantine);
        }
        catch (RecoveryAuthorityStaleException)
        {
            if (replacement is null)
            {
                return RecoveryAttemptResult.Stop;
            }
            var rejection = await RejectReplacementUnderGateAsync(
                    request,
                    replacement,
                    storage,
                    CloseRecoveryAuthority(request, replacement),
                    "restart was suspended because stale replacement cleanup could not be proven")
                .ConfigureAwait(false);
            return rejection.IntegrityFailure
                ? RecoveryAttemptResult.Stop
                : rejection.Safe
                ? RecoveryAttemptResult.Stop
                : RecoveryAttemptResult.Suspend(
                    _sessions.Generation,
                    rejection.Message!,
                    rejection.Quarantine);
        }
        catch (Exception exception)
        {
            var failedAtUtc = _timeProvider.GetUtcNow();
            var authorityDrain = replacement is null
                ? Task.CompletedTask
                : CloseRecoveryAuthority(request, replacement);
            if (!_sessions.RecordPackageRecoveryFailure(request, exception, failedAtUtc))
            {
                if (replacement is null)
                {
                    return RecoveryAttemptResult.Stop;
                }
                var staleRejection = await RejectReplacementUnderGateAsync(
                        request,
                        replacement,
                        storage,
                        authorityDrain,
                        "restart was suspended because stale replacement cleanup could not be proven")
                    .ConfigureAwait(false);
                return staleRejection.IntegrityFailure
                    ? RecoveryAttemptResult.Stop
                    : staleRejection.Safe
                    ? RecoveryAttemptResult.Stop
                    : RecoveryAttemptResult.Suspend(
                        _sessions.Generation,
                        staleRejection.Message!,
                        staleRejection.Quarantine);
            }

            failureCount = RecordFailure(request, failedAtUtc);
            if (replacement is not null)
            {
                var rejection = await RejectReplacementUnderGateAsync(
                        request,
                        replacement,
                        storage,
                        authorityDrain,
                        "restart was suspended because rejected replacement cleanup could not be proven")
                    .ConfigureAwait(false);
                if (rejection.IntegrityFailure)
                {
                    return RecoveryAttemptResult.Stop;
                }
                if (!rejection.Safe)
                {
                    return RecoveryAttemptResult.Suspend(
                        _sessions.Generation,
                        rejection.Message!,
                        rejection.Quarantine);
                }
            }
            _logger.LogWarning(
                exception,
                "Package {PackageId} Worker V1 recovery failed with rolling failure count {FailureCount}",
                request.PackageId,
                failureCount);
            return failureCount >= _policy.SuspensionFailureCount
                ? RecoveryAttemptResult.Suspend(
                    _sessions.Generation,
                    $"Package '{request.PackageId}' restart was suspended after {_policy.SuspensionFailureCount} failures within {_policy.FailureWindow.TotalSeconds:0} seconds.")
                : RecoveryAttemptResult.Retry(failureCount);
        }
    }

    private static void ValidateReplacement(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement)
    {
        if (replacement.RuntimeActivationId == Guid.Empty
            || replacement.RuntimeActivationId == request.FailedActivationId
            || ReferenceEquals(replacement.Worker, request.FailedPackage.Worker)
            || !string.Equals(
                replacement.Descriptor.PackageId,
                request.PackageId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                replacement.Descriptor.Version,
                request.FailedPackage.Descriptor.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Package '{request.PackageId}' recovery did not create a fresh exact worker activation.");
        }
    }

    private async Task<ActiveLoadedPackage> ActivateReplacementAsync(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement,
        PackageStorageActivationTransaction storage,
        CancellationToken cancellationToken)
    {
        var validation = _sessions.State.BeginPackageRecoveryActivation(request, replacement);
        if (validation == RuntimePackageRecoveryValidation.DependencyUnavailable)
        {
            throw new RecoveryDependencyUnavailableException();
        }
        if (validation == RuntimePackageRecoveryValidation.Stale)
        {
            throw new RecoveryAuthorityStaleException();
        }
        _rpcCatalog.PreparePackageRecovery(
            replacement,
            _sessions.Generation,
            request.ActivationGeneration);
        await RunWithTimeoutAsync(
                token => replacement.Worker.StartAsync(token),
                _lifecyclePolicy.PackageWorkerStartupTimeout,
                $"Recovered package worker '{request.PackageId}' did not start",
                cancellationToken)
            .ConfigureAwait(false);
        replacement = request.Session.RefreshRecoveryWorkerMetadata(replacement);
        var generation = new RuntimePackageGeneration(
            replacement.RuntimeActivationId,
            request.ActivationGeneration);
        using var activationTimeout = new CancellationTokenSource(
            _lifecyclePolicy.PackageRuntimeGenerationActivationTimeout,
            _timeProvider);
        using var activation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            activationTimeout.Token);
        try
        {
            await replacement.Worker.CommitGenerationAsync(generation, activation.Token)
                .ConfigureAwait(false);
            await replacement.Worker.PrepareStorageMigrationsAsync(generation, activation.Token)
                .ConfigureAwait(false);
            await storage.ValidateMigrationPlansAsync(activation.Token).ConfigureAwait(false);
            await replacement.Worker.ActivateGenerationAsync(activation.Token).ConfigureAwait(false);
            await storage.WriteCompletionsAsync(activation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            activationTimeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Recovered package worker '{request.PackageId}' did not commit and activate within {_lifecyclePolicy.PackageRuntimeGenerationActivationTimeout.TotalSeconds:0.###} seconds.",
                exception);
        }
        var completion = replacement.Worker.GenerationCompletion
                         ?? throw new InvalidOperationException(
                             "A recovered package worker returned a null generation completion task.");
        if (completion.IsFaulted || completion.IsCanceled)
        {
            await completion.ConfigureAwait(false);
        }
        return replacement;
    }

    private Task CloseRecoveryAuthority(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement)
    {
        var packageCallsDrained = _sessions.State.AbandonPackageRecoveryActivation(
            request,
            replacement.RuntimeActivationId);
        var rpcCallsDrained = _rpcCatalog.DiscardPackageRecovery(
            request.PackageId,
            replacement.RuntimeActivationId);
        return Task.WhenAll(rpcCallsDrained, packageCallsDrained);
    }

    private async Task RunWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var task = Task.Run(() => operation(deadline.Token), CancellationToken.None);
        try
        {
            await task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeoutCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAsync(task);
            throw new TimeoutException($"{timeoutMessage} within {timeout.TotalSeconds:0.###} seconds.", exception);
        }
    }

    private async Task<RecoveryRejectionResult> RejectReplacementUnderGateAsync(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement,
        PackageStorageActivationTransaction? storage,
        Task authorityDrain,
        string suspensionReason)
    {
        try
        {
            await authorityDrain.WaitAsync(_lifecyclePolicy.PackageWorkerCleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            _logger.LogError(
                exception,
                "Rejected recovery authority for package {PackageId} did not drain; recovery is suspended",
                request.PackageId);
            return RecoveryRejectionResult.Unsafe(
                $"Package '{request.PackageId}' {suspensionReason}.",
                new PendingRecoveryQuarantine(
                    request.PackageId,
                    replacement,
                    storage,
                    authorityDrain,
                    CleanupCompletion: null));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Rejected recovery authority callbacks for package {PackageId} failed after authority drained; cleanup will continue",
                request.PackageId);
        }

        try
        {
            await RuntimeWorkerLifecycle.ProveStoppedAndDisposedAsync(
                    replacement.Worker,
                    request.PackageId,
                    _logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (RuntimeWorkerCleanupUnprovenException exception)
        {
            _logger.LogError(
                exception,
                "Rejected recovery worker cleanup for package {PackageId} could not be proven; recovery is suspended",
                request.PackageId);
            return RecoveryRejectionResult.Unsafe(
                $"Package '{request.PackageId}' {suspensionReason}.",
                new PendingRecoveryQuarantine(
                    request.PackageId,
                    replacement,
                    storage,
                    authorityDrain,
                    exception.CleanupCompletion));
        }
        if (storage is not null)
        {
            try
            {
                await _rollbackAsync(storage).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SignalRuntimeIntegrityFailure(request.PackageId, exception);
                return RecoveryRejectionResult.RuntimeIntegrityFailure;
            }
        }
        return RecoveryRejectionResult.Completed;
    }

    private void TrackQuarantinedRecovery(
        PendingRecoveryQuarantine pending,
        RuntimeOperationGate.Lease operation)
    {
        var quarantine = new QuarantinedRecovery(pending, operation);
        lock (_stateGate)
        {
            _quarantinedRecoveries.Add(quarantine);
            quarantine.SetContinuation(ContinueQuarantinedRecoveryAsync(quarantine));
        }
        quarantine.Start();
    }

    private async Task ContinueQuarantinedRecoveryAsync(QuarantinedRecovery quarantine)
    {
        await quarantine.Started.ConfigureAwait(false);
        try
        {
            try
            {
                await quarantine.Pending.AuthorityDrain.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Quarantined recovery authority callbacks for package {PackageId} failed after authority drained; cleanup will continue",
                    quarantine.Pending.PackageId);
            }

            if (quarantine.Pending.CleanupCompletion is { } cleanupCompletion)
            {
                await cleanupCompletion.ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await RuntimeWorkerLifecycle.ProveStoppedAndDisposedAsync(
                            quarantine.Pending.Replacement.Worker,
                            quarantine.Pending.PackageId,
                            _logger,
                            _lifecyclePolicy.PackageWorkerCleanupTimeout)
                        .ConfigureAwait(false);
                }
                catch (RuntimeWorkerCleanupUnprovenException exception)
                {
                    await exception.CleanupCompletion.ConfigureAwait(false);
                }
            }

        }
        catch (Exception exception)
        {
            _logger.LogCritical(
                exception,
                "Quarantined recovery cleanup for package {PackageId} could not be proven; the Runtime operation fence remains held",
                quarantine.Pending.PackageId);
            return;
        }

        try
        {
            if (quarantine.Pending.Storage is not null)
            {
                await _rollbackAsync(quarantine.Pending.Storage).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            SignalRuntimeIntegrityFailure(quarantine.Pending.PackageId, exception);
        }
        finally
        {
            lock (_stateGate)
            {
                _quarantinedRecoveries.Remove(quarantine);
            }
            await quarantine.Operation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SignalRuntimeIntegrityFailure(string packageId, Exception exception)
    {
        if (Interlocked.Exchange(ref _integrityFailureSignaled, 1) != 0)
        {
            return;
        }

        _logger.LogCritical(
            exception,
            "Recovery storage rollback for package {PackageId} failed; Runtime integrity is uncertain and host shutdown was requested",
            packageId);
        try
        {
            _operationGate.FenceForIntegrityFailure();
        }
        catch (Exception fenceException)
        {
            _logger.LogCritical(
                fenceException,
                "Failed to fence Runtime lifecycle operations after a storage rollback failure");
        }
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception cancellationException)
        {
            _logger.LogCritical(
                cancellationException,
                "Failed to cancel package recovery after a storage rollback failure");
        }
        try
        {
            _requestHostStop?.Invoke();
        }
        catch (Exception stopException)
        {
            _logger.LogCritical(
                stopException,
                "Failed to request Runtime host shutdown after a storage rollback failure");
        }
    }

    private async Task ObserveQuarantinesDuringShutdownAsync(CancellationToken cancellationToken)
    {
        Task[] continuations;
        lock (_stateGate)
        {
            continuations = _quarantinedRecoveries
                .Select(static quarantine => quarantine.Continuation)
                .ToArray();
        }
        if (continuations.Length == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_lifecyclePolicy.ShutdownTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await Task.WhenAll(continuations).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Runtime shutdown stopped waiting for {QuarantineCount} quarantined package recoveries; their operation fences remain held until cleanup completes",
                continuations.Length);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "One or more quarantined package recovery continuations faulted during Runtime shutdown");
        }
    }

    private async Task PublishSuspendedIfCurrentAsync(
        RuntimePackageRecoveryRequest request,
        CancellationToken cancellationToken,
        string? message = null)
    {
        long? generation = null;
        try
        {
            await using (var operation = await _operationGate.EnterAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                if (_sessions.State.ValidatePackageRecovery(request) != RuntimePackageRecoveryValidation.Stale)
                {
                    generation = _sessions.Generation;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (
            cancellationToken.IsCancellationRequested
            || _shutdown.IsCancellationRequested)
        {
        }
        if (generation is not null)
        {
            PublishSuspensionEvent(
                request,
                generation.Value,
                message
                ?? $"Package '{request.PackageId}' restart was suspended after {_policy.SuspensionFailureCount} failures within {_policy.FailureWindow.TotalSeconds:0} seconds.");
        }
    }

    private void PublishSuspensionEvent(
        RuntimePackageRecoveryRequest request,
        long generation,
        string message)
        => _events.PublishPackageRecovery(
            RuntimeEventKind.PackageRecoverySuspended,
            generation,
            request.PackageId,
            success: false,
            message);

    private int RecordFailure(RuntimePackageRecoveryRequest request, DateTimeOffset failedAtUtc)
    {
        lock (_stateGate)
        {
            return RecordFailureLocked(request, failedAtUtc);
        }
    }

    private int RecordFailureLocked(RuntimePackageRecoveryRequest request, DateTimeOffset failedAtUtc)
    {
        var key = new RecoveryHistoryKey(request.SessionIdentity, request.PackageId);
        if (!_failureHistory.TryGetValue(key, out var failures))
        {
            failures = new Queue<DateTimeOffset>();
            _failureHistory.Add(key, failures);
            _failureHistoryOrder.Enqueue(key);
            while (_failureHistoryOrder.Count > MaximumRecoveryHistories)
            {
                _failureHistory.Remove(_failureHistoryOrder.Dequeue());
            }
        }
        PruneFailures(failures, failedAtUtc);
        failures.Enqueue(failedAtUtc);
        return failures.Count;
    }

    private void PruneFailures(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        var cutoff = now - _policy.FailureWindow;
        while (failures.TryPeek(out var failure) && failure < cutoff)
        {
            failures.Dequeue();
        }
    }

    private async Task RemoveCompletedRequestAsync(RecoveryRequestKey key, Task recovery)
    {
        await ObserveAsync(recovery).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (_activeRequests.TryGetValue(key, out var current)
                && ReferenceEquals(current, recovery))
            {
                _activeRequests.Remove(key);
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task ObserveCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static RuntimePackageRecoveryPolicyOptions ValidatePolicy(
        RuntimePackageRecoveryPolicyOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.FailureWindow <= TimeSpan.Zero
            || policy.DependencyRetryDelay <= TimeSpan.Zero
            || policy.SuspensionFailureCount < 2
            || policy.RestartDelays.Count < policy.SuspensionFailureCount - 1
            || policy.RestartDelays.Any(static delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "The Runtime package recovery policy is invalid.");
        }
        return policy;
    }

    private readonly record struct RecoveryRequestKey(
        object SessionIdentity,
        string PackageId,
        Guid FailedActivationId);

    private readonly record struct RecoveryHistoryKey(
        object SessionIdentity,
        string PackageId);

    private enum RecoveryAttemptDisposition
    {
        Stop,
        WaitForDependency,
        Retry,
        Suspend,
    }

    private sealed record RecoveryAttemptResult(
        RecoveryAttemptDisposition Disposition,
        int FailureCount = 0,
        long Generation = 0,
        string? Message = null,
        PendingRecoveryQuarantine? Quarantine = null)
    {
        public static RecoveryAttemptResult Stop { get; } = new(RecoveryAttemptDisposition.Stop);
        public static RecoveryAttemptResult WaitForDependency { get; } = new(RecoveryAttemptDisposition.WaitForDependency);
        public static RecoveryAttemptResult Retry(int failureCount)
            => new(RecoveryAttemptDisposition.Retry, failureCount);
        public static RecoveryAttemptResult Suspend(
            long generation,
            string message,
            PendingRecoveryQuarantine? quarantine = null)
            => new(
                RecoveryAttemptDisposition.Suspend,
                Generation: generation,
                Message: message,
                Quarantine: quarantine);
    }

    private sealed record RecoveryRejectionResult(
        bool Safe,
        bool IntegrityFailure,
        string? Message,
        PendingRecoveryQuarantine? Quarantine)
    {
        public static RecoveryRejectionResult Completed { get; } = new(true, false, null, null);
        public static RecoveryRejectionResult RuntimeIntegrityFailure { get; } = new(false, true, null, null);
        public static RecoveryRejectionResult Unsafe(
            string message,
            PendingRecoveryQuarantine? quarantine = null)
            => new(false, false, message, quarantine);
    }

    private sealed record PendingRecoveryQuarantine(
        string PackageId,
        ActiveLoadedPackage Replacement,
        PackageStorageActivationTransaction? Storage,
        Task AuthorityDrain,
        Task? CleanupCompletion);

    private sealed class QuarantinedRecovery(
        PendingRecoveryQuarantine pending,
        RuntimeOperationGate.Lease operation)
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRecoveryQuarantine Pending { get; } = pending;
        public RuntimeOperationGate.Lease Operation { get; } = operation;
        public Task Started => _started.Task;
        public Task Continuation { get; private set; } = Task.CompletedTask;

        public void SetContinuation(Task continuation)
            => Continuation = continuation;

        public void Start() => _started.TrySetResult();
    }

    private sealed class RecoveryDependencyUnavailableException : Exception
    {
    }

    private sealed class RecoveryAuthorityStaleException : Exception
    {
    }
}
