using System.Diagnostics;
using System.IO.Pipes;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal interface IRuntimeWorkerConnectionSource
{
    RuntimeWorkerConnection? GetWorkerConnection();
}

internal sealed class RuntimeWorkerCoordinator : IRuntimeWorkerConnectionSource, IAsyncDisposable
{
    private const int MaximumOperationMessageLength = 2048;
    private static readonly TimeSpan DefaultWorkerShutdownTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultTerminationWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastWorkerShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FastTerminationWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastRuntimeLeaseTimeout = TimeSpan.FromSeconds(2);
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HostLifecycleStore _lifecycleStore;
    private readonly string? _runtimeHostPath;
    private readonly string _connectionInfoPath;
    private readonly string _runtimeStateRoot;
    private readonly TimeSpan _startupTimeout;
    private readonly Func<bool> _isRuntimeLeaseAvailable;
    private readonly Func<int, TimeSpan> _getRecoveryDelay;
    private readonly Queue<DateTimeOffset> _recentCrashes = new();
    private Process? _process;
    private bool _lifecycleOwnsProcessExit;
    private bool _processExitObserved;
    private AnonymousPipeServerStream? _supervisorLifetimePipe;
    private RuntimeWorkerEndpointLease? _workerEndpoint;
    private RuntimeWorkerConnection? _connection;
    private long _workerEpoch;
    private HostLifecyclePersistentState _persistentState;
    private HostRuntimeDesiredState _desiredState;
    private HostRuntimeState _state = HostRuntimeState.Stopped;
    private long _deploymentGeneration;
    private Guid? _runtimeInstanceId;
    private DateTimeOffset? _runtimeStartedAtUtc;
    private string? _activeVersion;
    private string? _previousVersion;
    private string? _failureCode;
    private string? _failureMessage;
    private Task? _activeOperationTask;
    private Task? _workerMonitorTask;
    private bool _recoveryScheduled;
    private int _disposeStarted;
    private bool _disposing;
    private long _workerShutdownTimeoutTicks = DefaultWorkerShutdownTimeout.Ticks;
    private long _terminationWaitTimeoutTicks = DefaultTerminationWaitTimeout.Ticks;
    private long _runtimeLeaseTimeoutTicks;

    public RuntimeWorkerCoordinator(
        string? runtimeHostPath,
        string connectionInfoPath,
        TimeSpan startupTimeout,
        HostLifecycleStore lifecycleStore,
        Func<bool>? isRuntimeLeaseAvailable = null,
        Func<int, TimeSpan>? getRecoveryDelay = null,
        string? runtimeStateRoot = null)
    {
        _runtimeHostPath = string.IsNullOrWhiteSpace(runtimeHostPath) ? null : Path.GetFullPath(runtimeHostPath);
        _connectionInfoPath = Path.GetFullPath(connectionInfoPath);
        _runtimeStateRoot = Path.GetFullPath(runtimeStateRoot ?? RuntimeLocalState.GetV1RootPath());
        _startupTimeout = startupTimeout;
        _runtimeLeaseTimeoutTicks = startupTimeout.Ticks;
        _isRuntimeLeaseAvailable = isRuntimeLeaseAvailable ?? (() => RuntimeLocalState.IsLeaseAvailable(_runtimeStateRoot));
        _getRecoveryDelay = getRecoveryDelay
            ?? (failureCount => TimeSpan.FromMilliseconds(250 * (1 << Math.Min(failureCount - 1, 4))));
        _lifecycleStore = lifecycleStore;
        _persistentState = lifecycleStore.LoadOrCreate();
        _desiredState = _persistentState.DesiredState;
        _deploymentGeneration = _persistentState.DeploymentGeneration;
        _activeVersion = _persistentState.ActiveVersion;
        _previousVersion = _persistentState.PreviousVersion;
    }

    public RuntimeWorkerConnection? GetWorkerConnection()
    {
        lock (_stateGate)
        {
            return _state == HostRuntimeState.Ready
                   && _connection is not null
                   && _process is not null
                   && !_process.HasExited
                ? _connection
                : null;
        }
    }

    public void PrepareForFastShutdown()
    {
        Interlocked.Exchange(ref _workerShutdownTimeoutTicks, FastWorkerShutdownTimeout.Ticks);
        Interlocked.Exchange(ref _terminationWaitTimeoutTicks, FastTerminationWaitTimeout.Ticks);
        Interlocked.Exchange(ref _runtimeLeaseTimeoutTicks, FastRuntimeLeaseTimeout.Ticks);
    }

    public HostRuntimeStatus GetStatus()
    {
        lock (_stateGate)
        {
            return new HostRuntimeStatus(
                _desiredState,
                _state,
                _deploymentGeneration,
                _activeVersion,
                _previousVersion,
                _runtimeInstanceId,
                _runtimeStartedAtUtc,
                _failureCode,
                _failureMessage,
                _persistentState.ActiveOperationId);
        }
    }

    public Task<HostLifecycleSubmission> SubmitStartAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitOperationAsync(
            request,
            HostOperationKinds.RuntimeStart,
            HostRuntimeDesiredState.Running,
            cancellationToken);

    public Task<HostLifecycleSubmission> SubmitStopAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitOperationAsync(
            request,
            HostOperationKinds.RuntimeStop,
            HostRuntimeDesiredState.Stopped,
            cancellationToken);

    public Task<HostLifecycleSubmission> SubmitRestartAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitOperationAsync(
            request,
            HostOperationKinds.RuntimeRestart,
            HostRuntimeDesiredState.Running,
            cancellationToken);

    public bool TryGetOperation(string operationId, out HostOperationDescriptor? operation)
    {
        lock (_stateGate)
        {
            operation = _persistentState.Operations.FirstOrDefault(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            return operation is not null;
        }
    }

    public async Task<HostOperationDescriptor> WaitForOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetOperation(operationId, out var operation) || operation is null)
            {
                throw new KeyNotFoundException($"Host lifecycle operation '{operationId}' was not found.");
            }
            if (IsTerminal(operation.State))
            {
                return operation;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnsureStoppedDurablyAsync(CancellationToken cancellationToken = default)
    {
        var mutationId = Guid.NewGuid();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = GetStatus();
            if (status.ActiveOperationId is not null)
            {
                await WaitForOperationAsync(status.ActiveOperationId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var submission = await SubmitStopAsync(
                        new HostLifecycleRequest(mutationId, status.DeploymentGeneration),
                        cancellationToken)
                    .ConfigureAwait(false);
                var operation = await WaitForOperationAsync(
                        submission.Operation.OperationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (operation.State != HostOperationState.Succeeded)
                {
                    throw new InvalidOperationException(
                        operation.Message ?? $"Runtime stop operation failed with state '{operation.State}'.");
                }
                return;
            }
            catch (HostLifecycleConflictException exception) when (
                exception.Code is "host.deployment-generation-conflict" or "host.operation-active")
            {
                if (exception.ActiveOperationId is not null)
                {
                    await WaitForOperationAsync(exception.ActiveOperationId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task ReconcileStartupAsync(CancellationToken cancellationToken)
    {
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var submissionGateHeld = true;
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var activeOperation = GetActiveOperation();
                if (activeOperation is not null)
                {
                    _submissionGate.Release();
                    submissionGateHeld = false;
                    await ExecuteOperationCoreAsync(activeOperation.OperationId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (_desiredState == HostRuntimeDesiredState.Running)
                {
                    await StartCoreAsync(completingOperationId: null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WaitForRuntimeLeaseAsync(cancellationToken).ConfigureAwait(false);
                DeleteStaleConnection();
                lock (_stateGate)
                {
                    _state = HostRuntimeState.Stopped;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            if (submissionGateHeld)
            {
                _submissionGate.Release();
            }
        }
    }

    public async Task StopForSupervisorShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopCoreAsync(_desiredState, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    private async Task<HostLifecycleSubmission> SubmitOperationAsync(
        HostLifecycleRequest request,
        string kind,
        HostRuntimeDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostLifecycleSubmission submission;
            var schedule = false;
            lock (_stateGate)
            {
                var existing = _persistentState.Operations.FirstOrDefault(operation =>
                    operation.MutationId == request.MutationId);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Kind, kind, StringComparison.Ordinal)
                        || existing.ExpectedDeploymentGeneration != request.ExpectedDeploymentGeneration)
                    {
                        throw new HostLifecycleConflictException(
                            "host.mutation-reuse",
                            $"Mutation '{request.MutationId:D}' was already used for a different lifecycle request.");
                    }
                    submission = new HostLifecycleSubmission(existing, GetStatus());
                    return submission;
                }

                ValidateGeneration(request.ExpectedDeploymentGeneration);
                if (_persistentState.ActiveOperationId is not null)
                {
                    throw new HostLifecycleConflictException(
                        "host.operation-active",
                        $"Lifecycle operation '{_persistentState.ActiveOperationId}' is already active.",
                        _persistentState.ActiveOperationId);
                }

                var now = DateTimeOffset.UtcNow;
                var operation = new HostOperationDescriptor(
                    Guid.NewGuid().ToString("N"),
                    request.MutationId,
                    kind,
                    request.ExpectedDeploymentGeneration,
                    HostOperationState.Accepted,
                    now,
                    now,
                    null,
                    null);
                var updated = _persistentState with
                {
                    DesiredState = desiredState,
                    DeploymentGeneration = _deploymentGeneration + 1,
                    ActiveOperationId = operation.OperationId,
                    Operations = [.. _persistentState.Operations, operation],
                };
                _lifecycleStore.Save(updated);
                _persistentState = updated;
                _desiredState = desiredState;
                _deploymentGeneration = updated.DeploymentGeneration;
                _failureCode = null;
                _failureMessage = null;
                submission = new HostLifecycleSubmission(operation, GetStatus());
                schedule = true;
            }
            if (schedule)
            {
                ScheduleOperation(submission.Operation.OperationId);
            }
            return submission;
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    private void ScheduleOperation(string operationId)
    {
        var task = ExecuteScheduledOperationAsync(operationId);
        lock (_stateGate)
        {
            _activeOperationTask = task;
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_activeOperationTask, completed))
                    {
                        _activeOperationTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ExecuteScheduledOperationAsync(string operationId)
    {
        try
        {
            await _lifecycleGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ExecuteOperationCoreAsync(operationId, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ExecuteOperationCoreAsync(string operationId, CancellationToken cancellationToken)
    {
        var operation = GetRequiredOperation(operationId);
        if (IsTerminal(operation.State))
        {
            return;
        }

        try
        {
            if (operation.State == HostOperationState.Accepted)
            {
                operation = PersistOperation(
                    operation with
                    {
                        State = HostOperationState.Running,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    });
            }

            switch (operation.Kind)
            {
                case HostOperationKinds.RuntimeStart:
                    await StartCoreAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                    CompleteOperationIfActive(operation.OperationId, "Runtime worker is ready.");
                    break;
                case HostOperationKinds.RuntimeStop:
                    await StopCoreAsync(HostRuntimeDesiredState.Stopped, cancellationToken).ConfigureAwait(false);
                    CompleteOperationIfActive(operation.OperationId, "Runtime worker is stopped.");
                    break;
                case HostOperationKinds.RuntimeRestart:
                    lock (_stateGate)
                    {
                        _state = HostRuntimeState.Restarting;
                    }
                    try
                    {
                        await StopCoreAsync(HostRuntimeDesiredState.Running, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        RecordStartFailure(exception, cancellationToken);
                        throw;
                    }
                    await StartCoreAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                    CompleteOperationIfActive(operation.OperationId, "Runtime worker restarted.");
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Host lifecycle operation '{operation.Kind}'.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep the operation nonterminal so the next Supervisor instance can resume it.
        }
        catch (Exception exception)
        {
            var failureCode = GetFailureCode() ?? "host.lifecycle-operation-failed";
            try
            {
                CompleteOperation(
                    operationId,
                    HostOperationState.Failed,
                    Truncate(exception.Message),
                    failureCode);
            }
            catch (Exception persistenceException)
            {
                lock (_stateGate)
                {
                    _state = HostRuntimeState.Failed;
                    _failureCode = "host.lifecycle-state-save-failed";
                    _failureMessage = persistenceException.Message;
                }
            }
        }
    }

    private async Task<HostRuntimeStatus> StartCoreAsync(
        string? completingOperationId,
        CancellationToken cancellationToken)
    {
        var alreadyReady = false;
        lock (_stateGate)
        {
            if (_state == HostRuntimeState.Ready
                && _connection is not null
                && _process is not null
                && !_process.HasExited)
            {
                alreadyReady = true;
            }
            else if (_process is not null)
            {
                throw new HostLifecycleConflictException(
                    "host.worker-active",
                    "A Runtime worker process is active or awaiting exit cleanup and is not reusable.");
            }
            else
            {
                _state = HostRuntimeState.Starting;
                _failureCode = null;
                _failureMessage = null;
            }
        }

        if (alreadyReady)
        {
            if (completingOperationId is not null)
            {
                CompleteOperationIfActive(completingOperationId, "Runtime worker is ready.");
            }
            return GetStatus();
        }

        Process process;
        AnonymousPipeServerStream? lifetimePipe = null;
        RuntimeWorkerEndpointLease? endpointLease = null;
        var workerToken = RuntimeBearerToken.Create();
        try
        {
            await WaitForRuntimeLeaseAsync(cancellationToken).ConfigureAwait(false);
            DeleteStaleConnection();
            endpointLease = RuntimeWorkerEndpointLease.Create();
            lifetimePipe = CreateSupervisorLifetimePipe();
            process = StartWorkerProcess(workerToken, endpointLease.Endpoint, lifetimePipe);
        }
        catch (Exception exception)
        {
            lifetimePipe?.Dispose();
            endpointLease?.Dispose();
            RecordStartFailure(exception, cancellationToken);
            throw;
        }
        lock (_stateGate)
        {
            _process = process;
            _lifecycleOwnsProcessExit = true;
            _processExitObserved = false;
            _supervisorLifetimePipe = lifetimePipe;
            _workerEndpoint = endpointLease;
        }
        TrackWorkerMonitor(MonitorWorkerExitAsync(process, lifetimePipe));
        try
        {
            lifetimePipe.DisposeLocalCopyOfClientHandle();
            var (connection, handshake) = await WaitForReadyWorkerAsync(
                process,
                workerToken,
                endpointLease.Endpoint,
                cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (!ReferenceEquals(_process, process) || _processExitObserved || process.HasExited)
                {
                    throw new InvalidOperationException("Runtime worker exited or changed while startup was being verified.");
                }
                var activeVersion = handshake.Product.ProductVersion;
                var updated = _persistentState with
                {
                    DeploymentGeneration = completingOperationId is null
                        ? _deploymentGeneration + 1
                        : _deploymentGeneration,
                    PreviousVersion = string.Equals(_activeVersion, activeVersion, StringComparison.Ordinal)
                        ? _previousVersion
                        : _activeVersion,
                    ActiveVersion = activeVersion,
                };
                if (completingOperationId is not null
                    && string.Equals(updated.ActiveOperationId, completingOperationId, StringComparison.Ordinal))
                {
                    var operation = GetRequiredOperationCore(updated, completingOperationId);
                    updated = ReplaceOperation(
                        updated,
                        operation with
                        {
                            State = HostOperationState.Succeeded,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                            Message = "Runtime worker is ready.",
                            FailureCode = null,
                        },
                        activeOperationId: null);
                }
                _lifecycleStore.Save(updated);
                _persistentState = updated;
                _desiredState = updated.DesiredState;
                _deploymentGeneration = updated.DeploymentGeneration;
                _activeVersion = updated.ActiveVersion;
                _previousVersion = updated.PreviousVersion;
                _connection = new RuntimeWorkerConnection(
                    connection,
                    endpointLease.Endpoint,
                    ++_workerEpoch);
                _runtimeInstanceId = handshake.RuntimeInstanceId;
                _runtimeStartedAtUtc = DateTimeOffset.UtcNow;
                _state = HostRuntimeState.Ready;
                _lifecycleOwnsProcessExit = false;
            }
            return GetStatus();
        }
        catch (Exception exception)
        {
            TryTerminate(process);
            var terminated = await WaitForTerminatedProcessAsync(process).ConfigureAwait(false);
            if (!terminated)
            {
                lifetimePipe.Dispose();
                terminated = await WaitForTerminatedProcessAsync(process).ConfigureAwait(false);
            }
            if (!terminated)
            {
                lock (_stateGate)
                {
                    if (!ReferenceEquals(_process, process))
                    {
                        terminated = true;
                    }
                    else if (_processExitObserved || process.HasExited)
                    {
                        terminated = true;
                    }
                    else
                    {
                        _state = HostRuntimeState.Failed;
                        _failureCode = "host.worker-termination-failed";
                        _failureMessage = "Runtime worker did not terminate after failed startup.";
                        _lifecycleOwnsProcessExit = false;
                    }
                }
            }
            if (!terminated)
            {
                DeleteStaleConnection();
                throw new InvalidOperationException(
                    "Runtime worker did not terminate after failed startup; recovery is suspended until it exits.",
                    exception);
            }
            TimeSpan? recoveryDelay = null;
            lock (_stateGate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _lifecycleOwnsProcessExit = false;
                    _processExitObserved = false;
                    _supervisorLifetimePipe = null;
                    _workerEndpoint = null;
                    _connection = null;
                    _runtimeInstanceId = null;
                    _runtimeStartedAtUtc = null;
                    _state = HostRuntimeState.Failed;
                    _failureCode = exception is OperationCanceledException
                        ? "host.worker-start-cancelled"
                        : "host.worker-start-failed";
                    _failureMessage = exception.Message;
                    if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        recoveryDelay = RecordWorkerFailureForRecoveryCore();
                    }
                }
            }
            lifetimePipe.Dispose();
            endpointLease.Dispose();
            DeleteStaleConnection();
            process.Dispose();
            if (recoveryDelay is { } delay)
            {
                ScheduleRecovery(delay);
            }
            throw;
        }
    }

    private async Task StopCoreAsync(
        HostRuntimeDesiredState desiredStateAfterStop,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process? process;
        RuntimeWorkerConnection? connection;
        AnonymousPipeServerStream? lifetimePipe;
        RuntimeWorkerEndpointLease? endpointLease;
        var processAlreadyExited = false;
        lock (_stateGate)
        {
            _desiredState = desiredStateAfterStop;
            process = _process;
            connection = _connection;
            lifetimePipe = _supervisorLifetimePipe;
            endpointLease = _workerEndpoint;
            if (process is null)
            {
                _lifecycleOwnsProcessExit = false;
                _processExitObserved = false;
                _state = HostRuntimeState.Stopped;
                _connection = null;
                _runtimeInstanceId = null;
                _runtimeStartedAtUtc = null;
                _supervisorLifetimePipe = null;
                _workerEndpoint = null;
            }
            else if (process.HasExited)
            {
                processAlreadyExited = true;
                _process = null;
                _lifecycleOwnsProcessExit = false;
                _processExitObserved = false;
                _supervisorLifetimePipe = null;
                _workerEndpoint = null;
                _connection = null;
                _runtimeInstanceId = null;
                _runtimeStartedAtUtc = null;
                _state = HostRuntimeState.Stopped;
            }
            else
            {
                _lifecycleOwnsProcessExit = true;
                _processExitObserved = false;
                _state = HostRuntimeState.Stopping;
            }
        }

        if (process is null)
        {
            DeleteStaleConnection();
            lifetimePipe?.Dispose();
            endpointLease?.Dispose();
            await WaitForRuntimeLeaseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!processAlreadyExited)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GetWorkerShutdownTimeout());
            if (connection is not null)
            {
                using var transport = new RuntimeClientTransport(
                    () => connection.ConnectionInfo,
                    RuntimeIpcHttpMessageHandlerFactory.Create(connection.Endpoint));
                try
                {
                    await transport.ShutdownWithoutProtocolNegotiationAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                var terminated = await WaitForTerminatedProcessAsync(process).ConfigureAwait(false);
                if (!terminated)
                {
                    lifetimePipe?.Dispose();
                    terminated = await WaitForTerminatedProcessAsync(process).ConfigureAwait(false);
                }
                if (!terminated)
                {
                    lock (_stateGate)
                    {
                        if (!ReferenceEquals(_process, process))
                        {
                            terminated = true;
                        }
                        else if (_processExitObserved || process.HasExited)
                        {
                            terminated = true;
                        }
                        else
                        {
                            _state = HostRuntimeState.Failed;
                            _failureCode = "host.worker-termination-failed";
                            _failureMessage = "Runtime worker did not exit after termination was requested.";
                            _lifecycleOwnsProcessExit = false;
                        }
                    }
                }
                if (!terminated)
                {
                    DeleteConnectionIfMatches(connection);
                    throw new InvalidOperationException(
                        "Runtime worker did not exit after termination was requested; cleanup remains pending.");
                }
            }
        }
        lock (_stateGate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _lifecycleOwnsProcessExit = false;
                _processExitObserved = false;
                _supervisorLifetimePipe = null;
                _workerEndpoint = null;
                _connection = null;
                _runtimeInstanceId = null;
                _runtimeStartedAtUtc = null;
                _state = HostRuntimeState.Stopped;
            }
        }
        DeleteConnectionIfMatches(connection);
        lifetimePipe?.Dispose();
        endpointLease?.Dispose();
        process.Dispose();
        await WaitForRuntimeLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AnonymousPipeServerStream CreateSupervisorLifetimePipe()
        => new(
            PipeDirection.Out,
            HandleInheritability.Inheritable);

    private Process StartWorkerProcess(
        string workerToken,
        RuntimeIpcEndpoint endpoint,
        AnonymousPipeServerStream lifetimePipe)
    {
        if (_runtimeHostPath is null)
        {
            throw new FileNotFoundException("Sunder.Runtime.Host is not installed in this Host distribution.");
        }

        var isDotnetAssembly = string.Equals(Path.GetExtension(_runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : _runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_runtimeHostPath)!,
        };
        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(_runtimeHostPath);
        }
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = _connectionInfoPath;
        startInfo.Environment["SUNDER_RUNTIME_BEARER_TOKEN"] = workerToken;
        startInfo.Environment["SUNDER_RUNTIME_SUPERVISOR_PIPE"] = lifetimePipe.GetClientHandleAsString();
        startInfo.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = _runtimeStateRoot;
        startInfo.Environment[RuntimeIpcEndpoint.EnvironmentVariable] = endpoint.ToEnvironmentValue();
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start Sunder.Runtime.Host.");
    }

    private async Task<(RuntimeConnectionInfo Connection, RuntimeHandshakeResponse Handshake)> WaitForReadyWorkerAsync(
        Process process,
        string workerToken,
        RuntimeIpcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_startupTimeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Runtime worker exited with code {process.ExitCode} during startup.");
                }

                var connection = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
                if (connection is not null && FixedTimeTokenEquals(connection.BearerToken, workerToken))
                {
                    try
                    {
                        using var transport = new RuntimeClientTransport(
                            () => connection,
                            RuntimeIpcHttpMessageHandlerFactory.Create(endpoint));
                        var handshake = await transport.ProbeHandshakeAsync(deadline.Token).ConfigureAwait(false);
                        if (RuntimeProtocolCompatibility.GetIncompatibility(handshake) is not { } incompatibility)
                        {
                            using var management = new RuntimeManagementClient(transport);
                            var status = await management.GetSystemStatusAsync(deadline.Token).ConfigureAwait(false);
                            if (status?.IsReady == true)
                            {
                                return (connection, handshake);
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
                            continue;
                        }
                        throw new InvalidOperationException(incompatibility);
                    }
                    catch (HttpRequestException)
                    {
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Runtime worker did not become ready within {_startupTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task MonitorWorkerExitAsync(
        Process process,
        AnonymousPipeServerStream lifetimePipe)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        var recover = false;
        var recoveryDelay = TimeSpan.Zero;
        RuntimeWorkerConnection? exitedConnection;
        RuntimeWorkerEndpointLease? exitedEndpoint;
        var lifecycleOwnsExit = false;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }
            lifecycleOwnsExit = _lifecycleOwnsProcessExit;
            if (lifecycleOwnsExit)
            {
                _processExitObserved = true;
                exitedConnection = null;
                exitedEndpoint = null;
            }
            else
            {
                exitedConnection = _connection;
                exitedEndpoint = _workerEndpoint;
                _process = null;
                _lifecycleOwnsProcessExit = false;
                _processExitObserved = false;
                _supervisorLifetimePipe = null;
                _workerEndpoint = null;
                _connection = null;
                _runtimeInstanceId = null;
                _runtimeStartedAtUtc = null;
                if (_state != HostRuntimeState.CrashLoop)
                {
                    if (_state != HostRuntimeState.Failed)
                    {
                        _state = HostRuntimeState.Failed;
                        _failureCode = "host.worker-exited";
                        _failureMessage = $"Runtime worker exited with code {exitCode}.";
                    }
                    if (_desiredState == HostRuntimeDesiredState.Running && !_recoveryScheduled)
                    {
                        var delay = RecordWorkerFailureForRecoveryCore();
                        if (delay is not null)
                        {
                            recover = true;
                            recoveryDelay = delay.Value;
                        }
                    }
                }
            }
        }
        if (lifecycleOwnsExit)
        {
            return;
        }
        DeleteConnectionIfMatches(exitedConnection);
        lifetimePipe.Dispose();
        exitedEndpoint?.Dispose();
        process.Dispose();
        if (recover)
        {
            ScheduleRecovery(recoveryDelay);
        }
    }

    private async Task RecoverWorkerAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                _recoveryScheduled = false;
            }
            await _submissionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await _lifecycleGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    lock (_stateGate)
                    {
                        if (_disposing
                            || _desiredState != HostRuntimeDesiredState.Running
                            || _state != HostRuntimeState.Failed)
                        {
                            return;
                        }
                    }
                    await StartCoreAsync(completingOperationId: null, _lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            finally
            {
                _submissionGate.Release();
            }
        }
        catch (HostLifecycleConflictException exception) when (
            exception.Code == "host.worker-active" && !_lifetime.IsCancellationRequested)
        {
            // The worker monitor schedules recovery after the retained process actually exits.
        }
        catch
        {
            // StartCoreAsync records the recoverable worker failure in Host status.
        }
    }

    private TimeSpan? RecordWorkerFailureForRecoveryCore()
    {
        if (_disposing
            || _desiredState != HostRuntimeDesiredState.Running
            || _process is not null)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        while (_recentCrashes.TryPeek(out var crash)
               && now - crash > TimeSpan.FromMinutes(1))
        {
            _recentCrashes.Dequeue();
        }
        _recentCrashes.Enqueue(now);
        if (_recentCrashes.Count > 5)
        {
            _state = HostRuntimeState.CrashLoop;
            _failureCode = "host.worker-crash-loop";
            _failureMessage = "Runtime worker stopped repeatedly and automatic restart was suspended.";
            return null;
        }
        return _getRecoveryDelay(_recentCrashes.Count);
    }

    private void RecordStartFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TimeSpan? recoveryDelay;
        lock (_stateGate)
        {
            _state = HostRuntimeState.Failed;
            _failureCode = exception is TimeoutException
                ? "host.runtime-lease-timeout"
                : "host.worker-start-failed";
            _failureMessage = exception.Message;
            recoveryDelay = RecordWorkerFailureForRecoveryCore();
        }
        if (recoveryDelay is { } delay)
        {
            ScheduleRecovery(delay);
        }
    }

    private void ScheduleRecovery(TimeSpan delay)
    {
        lock (_stateGate)
        {
            if (_disposing
                || _recoveryScheduled
                || _state == HostRuntimeState.CrashLoop
                || _process is not null)
            {
                return;
            }
            _recoveryScheduled = true;
        }
        _ = RecoverWorkerAsync(delay);
    }

    private void TrackWorkerMonitor(Task monitor)
    {
        lock (_stateGate)
        {
            _workerMonitorTask = monitor;
        }
        _ = monitor.ContinueWith(
            completed =>
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_workerMonitorTask, completed))
                    {
                        _workerMonitorTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ValidateGeneration(long expectedDeploymentGeneration)
    {
        lock (_stateGate)
        {
            if (expectedDeploymentGeneration != _deploymentGeneration)
            {
                throw new HostLifecycleConflictException(
                    "host.deployment-generation-conflict",
                    $"Expected deployment generation {expectedDeploymentGeneration}, but the current generation is {_deploymentGeneration}.");
            }
        }
    }

    private HostOperationDescriptor? GetActiveOperation()
    {
        lock (_stateGate)
        {
            return _persistentState.ActiveOperationId is null
                ? null
                : GetRequiredOperationCore(_persistentState, _persistentState.ActiveOperationId);
        }
    }

    private HostOperationDescriptor GetRequiredOperation(string operationId)
    {
        lock (_stateGate)
        {
            return GetRequiredOperationCore(_persistentState, operationId);
        }
    }

    private static HostOperationDescriptor GetRequiredOperationCore(
        HostLifecyclePersistentState state,
        string operationId)
        => state.Operations.FirstOrDefault(operation =>
               string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"Host lifecycle operation '{operationId}' was not found.");

    private HostOperationDescriptor PersistOperation(HostOperationDescriptor operation)
    {
        lock (_stateGate)
        {
            var updated = ReplaceOperation(
                _persistentState,
                operation,
                _persistentState.ActiveOperationId);
            _lifecycleStore.Save(updated);
            _persistentState = updated;
            return operation;
        }
    }

    private void CompleteOperationIfActive(string operationId, string message)
    {
        lock (_stateGate)
        {
            if (!string.Equals(_persistentState.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                return;
            }
        }
        CompleteOperation(operationId, HostOperationState.Succeeded, message, failureCode: null);
    }

    private void CompleteOperation(
        string operationId,
        HostOperationState state,
        string? message,
        string? failureCode)
    {
        lock (_stateGate)
        {
            if (!string.Equals(_persistentState.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                return;
            }
            var operation = GetRequiredOperationCore(_persistentState, operationId);
            var completed = operation with
            {
                State = state,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Message = Truncate(message),
                FailureCode = Truncate(failureCode),
            };
            var updated = ReplaceOperation(_persistentState, completed, activeOperationId: null);
            _lifecycleStore.Save(updated);
            _persistentState = updated;
            if (state == HostOperationState.Failed && _state != HostRuntimeState.CrashLoop)
            {
                _state = HostRuntimeState.Failed;
                _failureCode = failureCode;
                _failureMessage = message;
            }
        }
    }

    private static HostLifecyclePersistentState ReplaceOperation(
        HostLifecyclePersistentState state,
        HostOperationDescriptor replacement,
        string? activeOperationId)
    {
        var replaced = false;
        var operations = state.Operations.Select(operation =>
        {
            if (!string.Equals(operation.OperationId, replacement.OperationId, StringComparison.Ordinal))
            {
                return operation;
            }
            replaced = true;
            return replacement;
        }).ToArray();
        if (!replaced)
        {
            throw new KeyNotFoundException($"Host lifecycle operation '{replacement.OperationId}' was not found.");
        }
        return state with
        {
            ActiveOperationId = activeOperationId,
            Operations = Array.AsReadOnly(operations),
        };
    }

    private string? GetFailureCode()
    {
        lock (_stateGate)
        {
            return _failureCode;
        }
    }

    private async Task WaitForRuntimeLeaseAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromTicks(Interlocked.Read(ref _runtimeLeaseTimeoutTicks));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!_isRuntimeLeaseAvailable())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Runtime state lease was not released within {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static bool IsTerminal(HostOperationState state)
        => state is HostOperationState.Succeeded
            or HostOperationState.Failed
            or HostOperationState.RolledBack
            or HostOperationState.Cancelled;

    private static string? Truncate(string? value)
        => value is null || value.Length <= MaximumOperationMessageLength
            ? value
            : value[..MaximumOperationMessageLength];

    private void DeleteStaleConnection()
    {
        var stale = RuntimeConnectionInfoStore.Load(_connectionInfoPath);
        if (stale is not null)
        {
            RuntimeConnectionInfoStore.DeleteIfMatches(stale, _connectionInfoPath);
        }
    }

    private void DeleteConnectionIfMatches(RuntimeWorkerConnection? connection)
    {
        if (connection is not null)
        {
            RuntimeConnectionInfoStore.DeleteIfMatches(connection.ConnectionInfo, _connectionInfoPath);
        }
    }

    private static bool FixedTimeTokenEquals(string left, string right)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private async Task<bool> WaitForTerminatedProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(GetTerminationWaitTimeout()).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private TimeSpan GetWorkerShutdownTimeout()
        => TimeSpan.FromTicks(Interlocked.Read(ref _workerShutdownTimeoutTicks));

    private TimeSpan GetTerminationWaitTimeout()
        => TimeSpan.FromTicks(Interlocked.Read(ref _terminationWaitTimeoutTicks));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        _disposing = true;
        _lifetime.Cancel();
        Task? activeOperationTask;
        lock (_stateGate)
        {
            activeOperationTask = _activeOperationTask;
        }
        if (activeOperationTask is not null)
        {
            try
            {
                await activeOperationTask.WaitAsync(GetTerminationWaitTimeout()).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        try
        {
            await StopForSupervisorShutdownAsync().ConfigureAwait(false);
        }
        catch
        {
            Process? process;
            AnonymousPipeServerStream? lifetimePipe;
            RuntimeWorkerEndpointLease? endpointLease;
            RuntimeWorkerConnection? connection;
            lock (_stateGate)
            {
                process = _process;
                lifetimePipe = _supervisorLifetimePipe;
                endpointLease = _workerEndpoint;
                connection = _connection;
                if (process is null)
                {
                    _lifecycleOwnsProcessExit = false;
                    _processExitObserved = false;
                }
                else
                {
                    _lifecycleOwnsProcessExit = true;
                    _processExitObserved = false;
                }
            }
            var terminated = process is null;
            if (process is not null)
            {
                TryTerminate(process);
                lifetimePipe?.Dispose();
                terminated = await WaitForTerminatedProcessAsync(process).ConfigureAwait(false);
            }
            if (!terminated && process is not null)
            {
                lock (_stateGate)
                {
                    if (!ReferenceEquals(_process, process))
                    {
                        terminated = true;
                    }
                    else if (_processExitObserved || process.HasExited)
                    {
                        terminated = true;
                    }
                    else
                    {
                        _lifecycleOwnsProcessExit = false;
                    }
                }
            }
            if (terminated)
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        _lifecycleOwnsProcessExit = false;
                        _processExitObserved = false;
                        _supervisorLifetimePipe = null;
                        _workerEndpoint = null;
                        _connection = null;
                    }
                }
                process?.Dispose();
                endpointLease?.Dispose();
            }
            else
            {
                lock (_stateGate)
                {
                    _state = HostRuntimeState.Failed;
                    _failureCode = "host.worker-termination-failed";
                    _failureMessage = "Runtime worker did not terminate during Supervisor disposal.";
                    _lifecycleOwnsProcessExit = false;
                }
            }
            DeleteConnectionIfMatches(connection);
            lifetimePipe?.Dispose();
        }
        Task? workerMonitorTask;
        lock (_stateGate)
        {
            workerMonitorTask = _workerMonitorTask;
        }
        if (workerMonitorTask is not null)
        {
            try
            {
                await workerMonitorTask.WaitAsync(GetTerminationWaitTimeout()).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _lifetime.Dispose();
        _submissionGate.Dispose();
        _lifecycleGate.Dispose();
    }
}

internal sealed class HostLifecycleConflictException(
    string code,
    string message,
    string? activeOperationId = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? ActiveOperationId { get; } = activeOperationId;
}
