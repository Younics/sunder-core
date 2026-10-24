using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker : IRuntimePackageWorker
{
    private const int StreamBufferCapacity = 32;
    private const string RpcExtendedDeadlineEnvironmentVariable = "SUNDER_RPC_EXTENDED_DEADLINE_V1";
    private readonly ILogger _logger;
    private readonly PreparedRuntimePackage _package;
    private readonly RuntimePackageDataResources _packageData;
    private readonly Guid _activationId;
    private readonly RuntimeRpcBroker _broker;
    private readonly RuntimeRpcCallerStamp _callerStamp;
    private readonly ResolvedExecutionRuntimeLaunch _launch;
    private readonly IExecutionRuntimeProcessLease _runtimeLease;
    private PackageProjectionAcquisition? _packageProjection;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private readonly PackageSessionState? _packageSessionState;
    private readonly RuntimeProcessPolicyOptions _policy;
    private readonly RuntimeResourcePolicy _resourcePolicy;
    private readonly string _resourceProfileHash;
    private readonly WorkerTransportLimits _transportLimits;
    private readonly CancellationToken _hostStopping;
    private readonly string _workerTemporaryPath;
    private readonly IReadOnlyList<ProviderIdentity> _expectedProviders;
    private readonly bool _rpcExtendedDeadlineV1;
    private readonly bool _rpcCallOwnershipV1;
    private readonly object _stateGate = new();
    private readonly object _callGate = new();
    private readonly object _writeQueueGate = new();
    private readonly Dictionary<string, HostCall> _hostCalls = new(StringComparer.Ordinal);
    private int _pendingHostCalls;
    private int _pendingHostStreams;
    private readonly Dictionary<string, WorkerCall> _workerCalls = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _workerCallCleanup = [];
    private readonly Dictionary<string, WorkerCallScope> _workerCallScopes = new(StringComparer.Ordinal);
    private int _closingWorkerCallScopes;
    private int _closingScopeContentHandles;
    private int _pendingMaterializedWorkerContentHandles;
    private int _releasingMaterializedWorkerContentHandles;
    private readonly HashSet<string> _rememberedWorkerIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedWorkerIdOrder = new();
    private readonly CancellationTokenSource _forceStop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _candidateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _generationCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _storageMigrationsPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _activationDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _generationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _forceStopGate = new();
    private readonly string _challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private Process? _process;
    private IExecutionRuntimeContainedProcess? _containedProcess;
    private Channel<byte[]>? _writes;
    private Task _readerTask = Task.CompletedTask;
    private Task _writerTask = Task.CompletedTask;
    private Task _stderrTask = Task.CompletedTask;
    private Task _exitTask = Task.CompletedTask;
    private Task? _forceStopCallbacks;
    private CancellationTokenRegistration _hostStopRegistration;
    private WorkerState _state;
    private WorkerState _shutdownAdmissionState;
    private long? _sessionGeneration;
    private string? _shutdownId;
    private Exception? _fault;
    private ProcessRuntimeGenerationFaultException? _reportedGenerationFault;
    private bool _activationAcknowledged;
    private byte[]? _activationFrameBody;
    private bool _shutdownAcknowledgementReceived;
    private bool _stopStarted;
    private long _queuedWriteBytes;

    public ProcessRuntimeWorker(
        ILogger logger,
        PreparedRuntimePackage package,
        RuntimePackageDataResources packageData,
        Guid activationId,
        RuntimeRpcBroker broker,
        ResolvedExecutionRuntimeLaunch launch,
        RuntimeProcessPolicyOptions? policy = null,
        CancellationToken hostStopping = default,
        PackageSessionState? packageSessionState = null,
        ExecutionRuntimePhaseTelemetry? telemetry = null)
    {
        PackageManifestAdmissionPolicy.EnsureSupported(
            package.Source.Manifest ?? throw new InvalidDataException("Package source is missing its manifest."));
        _logger = logger;
        _package = package;
        _packageData = packageData;
        _activationId = activationId;
        _broker = broker;
        _callerStamp = new RuntimeRpcCallerStamp(package.PackageId, activationId);
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        ValidateResolvedLaunch(package, _launch);
        _packageSessionState = packageSessionState;
        _policy = ValidatePolicy(policy ?? new RuntimeProcessPolicyOptions());
        _resourcePolicy = ResolveEffectiveResourcePolicy(_policy);
        _resourceProfileHash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(_resourcePolicy);
        _transportLimits = SunderWorkerProtocol.CreateTransportLimits(_resourcePolicy, _policy);
        _hostStopping = hostStopping;
        _workerTemporaryPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-worker",
            activationId.ToString("N"));
        _rpcCallOwnershipV1 = package.SelectedTarget?.RequiredHostCapabilities?.Contains(
            SunderSdkCapabilities.RpcCallOwnershipV1,
            StringComparer.Ordinal) == true;
        _rpcExtendedDeadlineV1 = _rpcCallOwnershipV1 || package.SelectedTarget?.RequiredHostCapabilities?.Contains(
            SunderSdkCapabilities.RpcExtendedDeadlineV1,
            StringComparer.Ordinal) == true;
        _expectedProviders = (package.Source.Manifest?.Provides ?? [])
            .Where(static provider => provider is not null
                                      && string.Equals(
                                          provider.Role,
                                          SunderPackageFormat.RuntimeHostRole,
                                          StringComparison.Ordinal))
            .Select(provider => new ProviderIdentity(
                provider!.ProviderId!,
                provider.ContractId!,
                provider.ContractVersion!,
                provider.DescriptorSha256!,
                provider.AbiSha256!))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
        _runtimeLease = _launch.TransferLease();
        _packageProjection = package.PackageProjectionLease?.Take();
    }

    public Task GenerationCompletion => _generationCompletion.Task;

    public IReadOnlyList<PackageStorageMigrationPlanIdentity> StorageMigrationPlans
    {
        get
        {
            lock (_stateGate) return _storageMigrationPlans;
        }
    }

    public PackageStorageMigrationJournal StorageMigrationJournal => _packageData.MigrationJournal;

    internal Task ActivationCompletion => _activated.Task;

    public Sunder.Sdk.Settings.PackageSettingsSchema? SettingsSchema { get; private set; }

    public string WorkspaceRootPath => _packageData.Storage.RoleLocalWorkspace.WorkspaceRootPath;

    internal string WorkerTemporaryPath => _workerTemporaryPath;

    public Task ComposeAsync(CancellationToken cancellationToken = default)
        => StartProcessAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WorkerState state;
        lock (_stateGate) state = _state;
        if (state == WorkerState.Created)
        {
            await ComposeAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (state == WorkerState.Starting)
        {
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await StartCandidateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_state != WorkerState.Created)
            {
                throw new InvalidOperationException("The process Runtime worker can only be started once.");
            }
            _state = WorkerState.Starting;
        }

        using var startupTimeout = new CancellationTokenSource(_policy.StartupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _hostStopping,
            startupTimeout.Token);
        try
        {
            _broker.RequirePackagePolicy(_package.Source);
            _package.StartupTrace?.RecordProcessStartRequested();
            var startInfo = CreateStartInfo();
            var containedProcess = await LaunchProcessAsync(startInfo, startup.Token).ConfigureAwait(false);
            var process = containedProcess.Process;
            DateTimeOffset? processStartedAtUtc = null;
            try
            {
                processStartedAtUtc = process.StartTime.ToUniversalTime();
            }
            catch
            {
                // Startup instrumentation must not change Worker launch behavior.
            }
            _package.StartupTrace?.RecordProcessStarted(process.Id, processStartedAtUtc);
            _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_policy.MaxWriteQueueMessages)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _writerTask = WriteLoopAsync(containedProcess.StandardInput);
            _readerTask = ReadLoopAsync(containedProcess.StandardOutput);
            _stderrTask = DrainStderrAsync(containedProcess.StandardError);
            _exitTask = MonitorExitAsync(process);
            _hostStopRegistration = _hostStopping.Register(static state =>
            {
                var worker = (ProcessRuntimeWorker)state!;
                _ = worker.StopFromHostAsync();
            }, this);

            QueueEnvelope(new
            {
                type = "host.hello",
                protocol = SunderWorkerProtocol.Name,
                protocolVersion = SunderWorkerProtocol.Version,
                challenge = _challenge,
                packageId = _package.PackageId,
                packageVersion = _package.Version,
                activationId = _activationId.ToString("N"),
                sessionId = _package.SessionId,
                providers = _expectedProviders.Select(static provider => provider.ToWire()).ToArray(),
                resourceProfile = SunderWorkerProtocol.ResourceProfileToWire(_resourcePolicy),
                resourceProfileHash = _resourceProfileHash,
            });

            await _ready.Task.WaitAsync(startup.Token).ConfigureAwait(false);

            lock (_stateGate)
            {
                ThrowIfFaulted();
                if (_state != WorkerState.Ready)
                {
                    throw new SunderWorkerProtocolException("Process Runtime worker entered an invalid startup state.");
                }
            }
        }
        catch (OperationCanceledException exception) when (
            startupTimeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !_hostStopping.IsCancellationRequested)
        {
            _package.StartupTrace?.CompleteWorkerReady("failed");
            await TerminateAfterFailureAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"Process Runtime target for package '{_package.PackageId}' did not become ready within {_policy.StartupTimeout.TotalSeconds:0.###} seconds.",
                exception);
        }
        catch (OperationCanceledException)
        {
            _package.StartupTrace?.CompleteWorkerReady("canceled");
            await TerminateAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            _package.StartupTrace?.CompleteWorkerReady("failed");
            await TerminateAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IExecutionRuntimeContainedProcess> LaunchProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var phase = _telemetry.BeginPhase(
            ExecutionRuntimePhaseNames.ProcessStart,
            _launch.Identity,
            correlationId: _launch.CorrelationId);
        try
        {
            var containedProcess = await _runtimeLease.LaunchAsync(
                    startInfo,
                    cancellationToken,
                    _packageProjection)
                .ConfigureAwait(false);
            _containedProcess = containedProcess;
            _process = containedProcess.Process;
            phase.Succeeded();
            return containedProcess;
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    private async Task StartCandidateAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Ready)
            {
                throw new InvalidOperationException("The V1 process Runtime worker candidate can only be started once after composition.");
            }
            _state = WorkerState.CandidateStarting;
        }

        _package.StartupTrace?.BeginCandidate();
        try
        {
            QueueEnvelope(new { type = "host.start-candidate" });
            await _candidateStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                ThrowIfFaulted();
                if (_state != WorkerState.CandidateStarted)
                {
                    throw new InvalidOperationException("The V1 process worker did not enter the candidate-started state.");
                }
            }
            _package.StartupTrace?.CompleteCandidate("succeeded");
        }
        catch (OperationCanceledException)
        {
            _package.StartupTrace?.CompleteCandidate("canceled");
            throw;
        }
        catch
        {
            _package.StartupTrace?.CompleteCandidate("failed");
            throw;
        }
    }

    public Task CommitGenerationAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default)
        => CommitGenerationCoreAsync(generation, cancellationToken);

    private async Task CommitGenerationCoreAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeGeneration(generation.SessionGeneration);
        if (generation.ActivationId != _activationId)
        {
            throw new InvalidOperationException("The process worker activation does not match the committed Runtime generation.");
        }

        var queueCommit = false;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state == WorkerState.Committed && _sessionGeneration == generation.SessionGeneration)
            {
                return;
            }
            if (_state == WorkerState.Committing && _sessionGeneration == generation.SessionGeneration)
            {
                // A publication retry waits for the original exact commit acknowledgement.
            }
            else if (_state == WorkerState.CandidateStarted && _sessionGeneration is null)
            {
                _sessionGeneration = generation.SessionGeneration;
                _state = WorkerState.Committing;
                queueCommit = true;
            }
            else
            {
                throw new InvalidOperationException("The V1 process worker is not ready to commit this Runtime generation.");
            }
        }

        if (queueCommit)
        {
            QueueEnvelope(new
            {
                type = "host.commit-generation",
                activationId = _activationId.ToString("N"),
                sessionGeneration = generation.SessionGeneration,
            });
        }
        await _generationCommitted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Committed || _sessionGeneration != generation.SessionGeneration)
            {
                throw new InvalidOperationException("The V1 process worker did not commit the requested Runtime generation.");
            }
        }
    }

    public async Task PrepareStorageMigrationsAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeGeneration(generation.SessionGeneration);
        if (generation.ActivationId != _activationId)
        {
            throw new InvalidOperationException(
                "The process worker activation does not match the storage-migration preparation generation.");
        }

        var queuePreparation = false;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state == WorkerState.StorageMigrationsPrepared
                && _sessionGeneration == generation.SessionGeneration)
            {
                return;
            }
            if (_state == WorkerState.PreparingStorageMigrations
                && _sessionGeneration == generation.SessionGeneration)
            {
                // Concurrent callers await the original exact preparation acknowledgement.
            }
            else if (_state == WorkerState.Committed
                     && _sessionGeneration == generation.SessionGeneration)
            {
                _state = WorkerState.PreparingStorageMigrations;
                queuePreparation = true;
            }
            else
            {
                throw new InvalidOperationException(
                    "The V1 process worker is not ready to prepare storage migrations for this generation.");
            }
        }

        if (queuePreparation)
        {
            QueueEnvelope(new
            {
                type = "host.prepare-storage-migrations",
                activationId = _activationId.ToString("N"),
                sessionGeneration = generation.SessionGeneration,
            });
        }
        await _storageMigrationsPrepared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.StorageMigrationsPrepared
                || _sessionGeneration != generation.SessionGeneration)
            {
                throw new InvalidOperationException(
                    "The V1 process worker did not prepare storage migrations for the requested Runtime generation.");
            }
        }
    }

    public async Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        var queueActivation = false;
        Exception? queueFailure = null;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state == WorkerState.Active)
            {
                return;
            }
            if (_state == WorkerState.Activating && _sessionGeneration is not null)
            {
                generation = _sessionGeneration.Value;
            }
            else if (_state == WorkerState.StorageMigrationsPrepared
                     && _sessionGeneration is not null)
            {
                generation = _sessionGeneration.Value;
                var body = SunderWorkerProtocol.SerializeFrameBody(new
                {
                    type = "host.activate",
                    sessionGeneration = generation,
                    workspaceRootPath = WorkspaceRootPath,
                }, _transportLimits);
                Volatile.Write(ref _activationFrameBody, body);
                if (!TryQueueFrameBody(body))
                {
                    Volatile.Write(ref _activationFrameBody, null);
                    queueFailure = WriteQueueFailure(body.Length);
                }
                else
                {
                    // Calls can observe Active only after host.activate is ordered ahead of them.
                    _state = WorkerState.Activating;
                    queueActivation = true;
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The process worker has not completed storage-migration preparation.");
            }
        }
        if (queueFailure is not null)
        {
            Fault(queueFailure);
            throw queueFailure;
        }
        if (queueActivation)
        {
            await _activationDispatched.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await _activated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active || _sessionGeneration != generation)
            {
                throw new InvalidOperationException(
                    "The process worker did not activate the committed Runtime generation.");
            }
        }
    }

    private static void EnsureSafeGeneration(long generation)
    {
        if (generation is < 0 or > SunderWorkerProtocol.MaximumSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                $"Runtime generations must be JavaScript-safe integers through {SunderWorkerProtocol.MaximumSafeInteger}.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        Task? existingStop = null;
        var releaseWithoutRunningProcess = false;
        lock (_stateGate)
        {
            if (_state is WorkerState.Stopped or WorkerState.Created)
            {
                if (_state == WorkerState.Created)
                {
                    _state = WorkerState.Stopping;
                }
                _stopStarted = true;
                _generationCompletion.TrySetResult();
                process = null;
                releaseWithoutRunningProcess = true;
            }
            else if (_stopStarted)
            {
                existingStop = _stopped.Task;
                process = null;
            }
            else
            {
                _stopStarted = true;
                if (_state != WorkerState.Faulted)
                {
                    if (_state != WorkerState.Stopping) _shutdownAdmissionState = _state;
                    _state = WorkerState.Stopping;
                }
                process = _process;
                _shutdownId ??= Guid.NewGuid().ToString("N");
            }
        }
        if (releaseWithoutRunningProcess)
        {
            try
            {
                await ReleaseExecutionRootsAsync().ConfigureAwait(false);
                CompleteExpectedStop();
            }
            catch (Exception exception)
            {
                ResetFailedStop(exception);
                throw;
            }
            return;
        }
        if (existingStop is not null)
        {
            await existingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        CancelWorkerCallsForShutdown();
        var scopeCleanup = ObserveAsync(CloseAllWorkerCallScopesAsync());
        if (process is null)
        {
            try
            {
                await scopeCleanup.ConfigureAwait(false);
                await ReleaseExecutionRootsAsync().ConfigureAwait(false);
                CompleteExpectedStop();
            }
            catch (Exception exception)
            {
                ResetFailedStop(exception);
                throw;
            }
            return;
        }

        try
        {
            try
            {
                if (!process.HasExited && CanDrainShutdown())
                {
                    QueueEnvelope(new
                    {
                        type = "host.shutdown",
                        shutdownId = _shutdownId,
                        reason = _hostStopping.IsCancellationRequested ? "host-stopping" : "activation-retired",
                    });
                }

                using var timeout = new CancellationTokenSource(_policy.ShutdownTimeout);
                using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    if (!process.HasExited && CanDrainShutdown())
                    {
                        // A generation fault may arrive after this wait starts. Always
                        // observe exit as well as ack, then finish reading its fault report.
                        var exit = process.WaitForExitAsync(shutdown.Token);
                        var terminal = await Task.WhenAny(_shutdownAcknowledged.Task, exit)
                            .WaitAsync(shutdown.Token).ConfigureAwait(false);
                        await terminal.ConfigureAwait(false);
                        if (ReferenceEquals(terminal, exit))
                            await _readerTask.WaitAsync(shutdown.Token).ConfigureAwait(false);
                        if (_reportedGenerationFault is null)
                            await _shutdownAcknowledged.Task.WaitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                    if (!process.HasExited)
                    {
                        await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                    await scopeCleanup.WaitAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    bool shutdownAcknowledged;
                    lock (_stateGate) shutdownAcknowledged = _shutdownAcknowledgementReceived;
                    _logger.LogWarning(
                        shutdownAcknowledged
                            ? "Process Runtime target for package {PackageId} acknowledged shutdown but did not exit within its deadline; killing its process tree"
                            : "Process Runtime target for package {PackageId} did not acknowledge shutdown within its deadline; killing its process tree",
                        _package.PackageId);
                    await TerminateContainedProcessAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writes?.Writer.TryComplete();
                _ = SignalForceStop();
                await TerminateContainedProcessAsync().ConfigureAwait(false);
                await ObserveAsync(_writerTask).ConfigureAwait(false);
                await ObserveAsync(_readerTask).ConfigureAwait(false);
                await ObserveAsync(_stderrTask).ConfigureAwait(false);
                await ObserveAsync(_exitTask).ConfigureAwait(false);
                if (scopeCleanup.IsCompleted)
                {
                    await scopeCleanup.ConfigureAwait(false);
                }
                CleanupAllWorkerPayloads();
                TryDeleteDirectory(_workerTemporaryPath);
                await ReleaseExecutionRootsAsync().ConfigureAwait(false);
                CompleteExpectedStop();
            }
        }
        catch (Exception exception)
        {
            ResetFailedStop(exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await ObserveAsync(CloseAllWorkerCallScopesAsync()).ConfigureAwait(false);
        CleanupAllWorkerContentFiles();
        CleanupAllWorkerPayloads();
        TryDeleteDirectory(_workerTemporaryPath);
        _hostStopRegistration.Dispose();
        _process?.Dispose();
        if (_containedProcess is not null)
        {
            await _containedProcess.DisposeAsync().ConfigureAwait(false);
        }
        RuntimeCancellation.DisposeAfterCallbacks(_forceStop, SignalForceStop());
        _packageData.Dispose();
    }

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = SerializeInvocation(
            HostCallKind.Unary,
            providerId,
            context,
            serviceId,
            methodId,
            request);
        ReservePendingHostCall(HostCallKind.Unary);
        var reservationHeld = true;
        HostCall? call = null;
        try
        {
            await WaitForInvocationReadinessAsync(cancellationToken).ConfigureAwait(false);
            call = AddHostCall(
                invocation.Id,
                HostCallKind.Unary,
                context,
                providerId,
                serviceId,
                methodId,
                invocation.Body,
                consumePendingReservation: true);
            reservationHeld = false;
            return await InvokeUnaryCoreAsync(
                call,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (call is { InvocationQueued: false }) RemoveUnwrittenHostCall(call);
            if (reservationHeld) ReleasePendingHostCall(HostCallKind.Unary);
        }
    }

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
        => InvokeServerStreamWhenReadyAsync(
            providerId,
            context,
            serviceId,
            methodId,
            request,
            cancellationToken);

    private async IAsyncEnumerable<JsonElement> InvokeServerStreamWhenReadyAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = SerializeInvocation(
            HostCallKind.ServerStream,
            providerId,
            context,
            serviceId,
            methodId,
            request);
        ReservePendingHostCall(HostCallKind.ServerStream);
        var reservationHeld = true;
        HostCall? call = null;
        try
        {
            await WaitForInvocationReadinessAsync(cancellationToken).ConfigureAwait(false);
            call = AddHostCall(
                invocation.Id,
                HostCallKind.ServerStream,
                context,
                providerId,
                serviceId,
                methodId,
                invocation.Body,
                consumePendingReservation: true);
            reservationHeld = false;
            await foreach (var item in InvokeServerStreamCoreAsync(
                call,
                cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            if (call is { InvocationQueued: false }) RemoveUnwrittenHostCall(call);
            if (reservationHeld) ReleasePendingHostCall(HostCallKind.ServerStream);
        }
    }

    private async ValueTask WaitForInvocationReadinessAsync(CancellationToken cancellationToken)
    {
        Task? activation = null;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state == WorkerState.Active)
            {
                return;
            }
            if (_state == WorkerState.Activating)
            {
                activation = _activated.Task;
            }
            else
            {
                throw WorkerActivating();
            }
        }

        await activation.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active)
            {
                throw WorkerActivating();
            }
        }
    }

    private static SunderRpcException WorkerActivating()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.worker.activating",
            "The process Runtime provider is still activating."));

    private async ValueTask<JsonElement> InvokeUnaryCoreAsync(
        HostCall call,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        return await call.UnaryCompletion.Task.ConfigureAwait(false);
    }

    private async IAsyncEnumerable<JsonElement> InvokeServerStreamCoreAsync(
        HostCall call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        try
        {
            await foreach (var item in call.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            if (!call.Terminal) CancelHostCall(call, cancellationToken);
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var selectedTarget = _package.SelectedTarget
                             ?? throw new InvalidOperationException(
                                 "A process Runtime target requires validated selected-target metadata.");
        var startInfo = new ProcessStartInfo
        {
            FileName = _launch.LauncherPath,
            WorkingDirectory = Path.GetFullPath(_package.ShadowFolder),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in _launch.ArgumentTokens)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        var temporaryPath = _workerTemporaryPath;
        Directory.CreateDirectory(temporaryPath);
        RestrictWorkerDirectory(temporaryPath);
        foreach (var variable in WorkerProcessEnvironment.CreateBaseline(
                     Environment.GetEnvironmentVariable,
                     temporaryPath,
                     WorkerProcessEnvironment.GetCurrentPlatform(),
                     WorkerProcessEnvironment.AllowsDockerEnvironment(
                         selectedTarget,
                         _package.ApprovedSensitiveCapabilities)))
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        startInfo.Environment["SUNDER_WORKER_PROTOCOL"] = SunderWorkerProtocol.Name;
        startInfo.Environment["SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1"] = "1";
        startInfo.Environment["SUNDER_PACKAGE_ID"] = _package.PackageId;
        startInfo.Environment["SUNDER_PACKAGE_VERSION"] = _package.Version;
        startInfo.Environment["SUNDER_ACTIVATION_ID"] = _activationId.ToString("N");
        startInfo.Environment["SUNDER_SESSION_ID"] = _package.SessionId;
        startInfo.Environment["SUNDER_PACKAGE_CONTENT_ROOT"] = Path.GetFullPath(_package.ShadowFolder);
        startInfo.Environment["SUNDER_WORKER_TEMP_ROOT"] = Path.GetFullPath(temporaryPath);
        if (_rpcExtendedDeadlineV1)
        {
            startInfo.Environment[RpcExtendedDeadlineEnvironmentVariable] = "1";
        }
        return startInfo;
    }

    private static void ValidateResolvedLaunch(
        PreparedRuntimePackage package,
        ResolvedExecutionRuntimeLaunch launch)
    {
        if (!Directory.Exists(launch.RuntimeRootPath)
            || !File.Exists(launch.LauncherPath)
            || package.EntryAssemblyPath is null
            || PathsEqual(launch.LauncherPath, package.EntryAssemblyPath)
            || launch.ArgumentTokens.Any(argument => string.Equals(
                argument,
                OfficialRuntimeSourcePolicy.EntryPointToken,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Package '{package.PackageId}' does not have a valid resolved execution-runtime launcher and closed argument recipe.");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RestrictWorkerDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private SerializedHostInvocation SerializeInvocation(
        HostCallKind kind,
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var body = SunderWorkerProtocol.SerializeFrameBody(new
            {
                type = "host.invoke",
                id,
                kind = kind == HostCallKind.Unary ? "unary" : "server-stream",
                providerId,
                serviceId,
                methodId,
                request,
                context = new
                {
                    callerPackageId = context.CallerPackageId,
                    callerPackageVersion = context.CallerPackageVersion,
                    deadlineUtc = context.DeadlineUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    callDepth = context.CallDepth,
                    provider = WorkerWire.ToWire(context.Provider),
                },
            }, _transportLimits);
            return new SerializedHostInvocation(id, body);
        }
        catch (WorkerTransportResourceException exception)
        {
            throw HostRequestResourceExhausted(exception);
        }
    }

    private HostCall AddHostCall(
        string id,
        HostCallKind kind,
        SunderRpcInvocationContext context,
        string providerId,
        string serviceId,
        string methodId,
        byte[] body,
        bool consumePendingReservation = false)
    {
        var call = new HostCall(id, kind, context, providerId, serviceId, methodId,
            _transportLimits.MaximumStreamQueueBytes);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active)
            {
                throw WorkerActivating();
            }
            lock (_callGate)
            {
                if (!consumePendingReservation) RequireHostCallCapacityUnderGate(kind);
                if (consumePendingReservation
                    && (kind == HostCallKind.ServerStream ? _pendingHostStreams : _pendingHostCalls) <= 0)
                    throw new InvalidOperationException("A process worker Host call reservation was not held.");
                _hostCalls.Add(call.Id, call);
                try
                {
                    // Fault withdrawal cannot miss this call or enqueue its cancellation
                    // before the original invocation has entered the ordered writer.
                    QueueHostCallBody(body);
                    call.InvocationQueued = true;
                }
                catch
                {
                    _hostCalls.Remove(call.Id);
                    throw;
                }
                if (consumePendingReservation)
                {
                    if (kind == HostCallKind.ServerStream) _pendingHostStreams--;
                    else _pendingHostCalls--;
                }
            }
        }
        return call;
    }

    private void RemoveUnwrittenHostCall(HostCall call)
    {
        lock (_callGate)
        {
            if (call.Terminal || !_hostCalls.Remove(call.Id)) return;
            call.Terminal = true;
            CleanupWorkerContentFiles(call);
            call.Events.Fail(HostRequestResourceExhausted());
        }
    }

    private void RequireHostCallCapacityUnderGate(HostCallKind kind)
    {
        var stream = _operationalOutcomesV1 && kind == HostCallKind.ServerStream;
        var count = _operationalOutcomesV1
            ? _hostCalls.Values.Count(call => call.Kind == kind) + (stream ? _pendingHostStreams : _pendingHostCalls + _contributionCalls.Count)
            : _hostCalls.Count + _pendingHostStreams + _pendingHostCalls + _contributionCalls.Count;
        if (count >= (stream ? _policy.MaxOutstandingHostStreams : _policy.MaxOutstandingHostCalls))
            throw SunderRpcException.Infrastructure(SunderRpcErrorKind.ResourceExhausted,
                    stream ? "rpc.worker.host-stream-limit" : "rpc.worker.host-call-limit",
                    stream ? "The process worker outstanding observation limit was reached."
                        : "The process worker outstanding call limit was reached.")
                .WithDispatchMetadata(SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe);
    }

    private void ReservePendingHostCall(HostCallKind kind)
    {
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state is not (WorkerState.Active or WorkerState.Activating))
            {
                throw WorkerActivating();
            }
            lock (_callGate)
            {
                RequireHostCallCapacityUnderGate(kind);
                if (kind == HostCallKind.ServerStream) _pendingHostStreams++;
                else _pendingHostCalls++;
            }
        }
    }

    private void ReleasePendingHostCall(HostCallKind kind)
    {
        lock (_callGate)
        {
            if (kind == HostCallKind.ServerStream)
            {
                if (_pendingHostStreams > 0) _pendingHostStreams--;
            }
            else if (_pendingHostCalls > 0) _pendingHostCalls--;
        }
    }

    private void CancelHostCall(HostCall call, CancellationToken cancellationToken)
    {
        lock (_callGate)
        {
            if (call.Terminal || call.Cancelled) return;
            call.Cancelled = true;
            call.UnaryCompletion.TrySetCanceled(cancellationToken);
            call.Events.Fail(new OperationCanceledException(cancellationToken));
        }
        TryQueueEnvelope(new { type = "host.cancel", id = call.Id });
        _ = EnforceCancellationDrainAsync(call);
    }

    private async Task EnforceCancellationDrainAsync(HostCall call)
    {
        try
        {
            await Task.Delay(_policy.CancellationDrainTimeout, _forceStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_callGate)
        {
            if (call.Terminal || !_hostCalls.ContainsKey(call.Id)) return;
        }
        Fault(new SunderWorkerProtocolException(
            $"Process worker did not terminate cancelled call '{call.Id}' within the cancellation drain deadline."));
    }

    private async Task ReadLoopAsync(Stream stdout)
    {
        try
        {
            while (!_forceStop.IsCancellationRequested)
            {
                using var document = await SunderWorkerProtocol.ReadFrameAsync(
                    stdout,
                    _transportLimits,
                    _forceStop.Token).ConfigureAwait(false);
                HandleEnvelope(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (_forceStop.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException exception)
        {
            if (!IsStopping()) Fault(exception);
        }
        catch (SunderWorkerProtocolException exception)
        {
            FaultProtocol(exception);
        }
        catch (Exception exception)
        {
            Fault(new SunderWorkerProtocolException("Process worker protocol reader failed.", exception));
        }
    }

    private void HandleEnvelope(JsonElement root)
    {
        var type = SunderWorkerProtocol.ReadEnvelopeType(root);
        if (!_ready.Task.IsCompleted)
        {
            if (!string.Equals(type, "worker.ready", StringComparison.Ordinal))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent '{type}' before the required ready envelope.");
            }
            HandleReady(root);
            return;
        }

        switch (type)
        {
            case "worker.result":
            case "worker.event":
            case "worker.complete":
            case "worker.error":
                HandleHostCallResponse(type, root);
                break;
            case "worker.get-provider":
            case "worker.report-invariant-violation":
            case "worker.discover":
            case "worker.watch":
            case "worker.invoke":
            case "worker.subscribe":
            case "worker.scope-open":
            case "worker.scope-close":
            case "worker.content-register":
            case "worker.content-open":
            case "worker.content-release":
            case "worker.payload-allocate":
            case "worker.payload-release":
            case "worker.settings-get":
            case "worker.settings-set":
            case "worker.settings-delete":
            case "worker.state-get":
            case "worker.state-set":
            case "worker.state-delete":
            case "worker.state-list":
            case "worker.migration-keys":
            case "worker.migration-apply":
            case "worker.secrets-get":
            case "worker.secrets-set":
            case "worker.secrets-delete":
            case "worker.files-read":
            case "worker.files-write":
            case "worker.files-delete":
            case "worker.logging-write":
            case "worker.provider-fault-diagnostic":
                BeginWorkerCall(type, root);
                break;
            case "worker.cancel":
                HandleWorkerCancellation(root);
                break;
            case "worker.activated":
                HandleActivated(ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseActivated(root));
                break;
            case "worker.candidate-started":
                HandleCandidateStarted(
                    ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseCandidateStarted(root));
                break;
            case "worker.generation-committed":
                HandleGenerationCommitted(
                    ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationCommitted(root));
                break;
            case "worker.storage-migrations-prepared":
                HandleStorageMigrationsPrepared(
                    ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseStorageMigrationsPrepared(root));
                break;
            case "worker.generation-fault":
                HandleGenerationFault(
                    ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationFault(root));
                break;
            case "worker.callback-started":
            case "worker.callback-completed":
            case "worker.callback-cancelled":
            case "worker.auth-status":
            case "worker.auth-connected":
            case "worker.auth-completed":
            case "worker.auth-disconnected":
            case "worker.contribution-error":
                HandleContributionResponse(type, root);
                break;
            case "worker.shutdown-ack":
                HandleShutdownAcknowledgement(
                    ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseShutdownAcknowledgement(root));
                break;
            default:
                throw new SunderWorkerProtocolException($"Unknown process worker envelope type '{type}'.");
        }
    }

    private void HandleReady(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "protocol",
            "protocolVersion",
            "challenge",
            "packageId",
            "packageVersion",
            "activationId",
            "sessionId",
            "resourceProfileHash",
            "contributions");
        if (!string.Equals(
                SunderWorkerProtocol.RequiredString(root, "protocol", 64),
                SunderWorkerProtocol.Name,
                StringComparison.Ordinal)
            || SunderWorkerProtocol.RequiredNonNegativeInt64(root, "protocolVersion") != SunderWorkerProtocol.Version
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "challenge", 128), _challenge, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "packageId", 256), _package.PackageId, StringComparison.Ordinal)
            || !string.Equals(
                SunderWorkerProtocol.RequiredString(root, "packageVersion", SemanticVersion.MaximumLength),
                _package.Version,
                StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "activationId", 64), _activationId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "sessionId", 256), _package.SessionId, StringComparison.Ordinal)
            || !string.Equals(
                SunderWorkerProtocol.RequiredString(root, "resourceProfileHash", 64),
                _resourceProfileHash,
                StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker ready envelope did not bind the exact protocol, challenge, package, activation, session, and resource profile.");
        }
        var contributions = ReadContributions(
            SunderWorkerProtocol.RequiredValue(root, "contributions"));
        SettingsSchema = contributions.SettingsSchema;
        ConfigureStorageMigrationPlans(contributions.StorageMigrations);
        ConfigureContributionHandlers(
            contributions.CallbackHandlerIds,
            contributions.HasAuthHandler);
        var providersElement = contributions.RpcProviders;
        if (providersElement.ValueKind != JsonValueKind.Array)
        {
            throw new SunderWorkerProtocolException("Process worker ready providers must be an array.");
        }
        var providers = providersElement.EnumerateArray().Select(ReadProviderIdentity)
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
        if (!providers.SequenceEqual(_expectedProviders))
        {
            throw new SunderWorkerProtocolException(
                "Process worker advertised providers do not exactly match the package's declared Runtime providers.");
        }
        _packageData.Settings.Schema = SettingsSchema;
        lock (_stateGate)
        {
            if (_state != WorkerState.Starting)
            {
                throw new SunderWorkerProtocolException(
                    "Process Runtime worker ready envelope arrived in an invalid state.");
            }
            _state = WorkerState.Ready;
            _package.StartupTrace?.CompleteWorkerReady("succeeded");
            _ready.TrySetResult();
        }
    }

    private static void RequireBoolean(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SunderWorkerProtocolException(
                $"Process worker contribution property '{propertyName}' must be a boolean.");
        }
    }

    private static ProviderIdentity ReadProviderIdentity(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "providerId",
            "contractId",
            "contractVersion",
            "descriptorSha256",
            "abiSha256");
        return new ProviderIdentity(
            SunderWorkerProtocol.RequiredString(value, "providerId", 256),
            SunderWorkerProtocol.RequiredString(value, "contractId", 256),
            SunderWorkerProtocol.RequiredString(value, "contractVersion", SemanticVersion.MaximumLength),
            ReadWorkerSha256(value, "descriptorSha256", requireLowercase: true),
            ReadWorkerSha256(value, "abiSha256", requireLowercase: true));
    }

    private static string ReadWorkerSha256(
        JsonElement value,
        string propertyName,
        bool requireLowercase)
    {
        var sha256 = SunderWorkerProtocol.RequiredString(value, propertyName, 64);
        if (sha256.Length != 64
            || sha256.Any(character => requireLowercase
                ? character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')
                : !char.IsAsciiHexDigit(character)))
        {
            throw new SunderWorkerProtocolException(
                $"Worker envelope property '{propertyName}' is not a canonical SHA-256 value.");
        }
        return sha256;
    }

    private void HandleCandidateStarted(WorkerCandidateStartedEnvelope _)
    {
        lock (_stateGate)
        {
            if (_state is not (WorkerState.CandidateStarting or WorkerState.Stopping)
                || _candidateStarted.Task.IsCompleted)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker acknowledged an invalid or duplicate candidate start.");
            }
            if (_state == WorkerState.CandidateStarting)
            {
                _state = WorkerState.CandidateStarted;
            }
        }
        _candidateStarted.TrySetResult();
    }

    private void HandleGenerationCommitted(WorkerGenerationCommittedEnvelope envelope)
    {
        lock (_stateGate)
        {
            if (_state is not (WorkerState.Committing or WorkerState.Stopping)
                || !string.Equals(envelope.ActivationId, _activationId.ToString("N"), StringComparison.Ordinal)
                || envelope.SessionGeneration != _sessionGeneration
                || _generationCommitted.Task.IsCompleted)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker acknowledged an invalid or duplicate Runtime generation commit.");
            }
            if (_state == WorkerState.Committing)
            {
                _state = WorkerState.Committed;
            }
        }
        _generationCommitted.TrySetResult();
    }

    private void HandleStorageMigrationsPrepared(WorkerStorageMigrationsPreparedEnvelope envelope)
    {
        lock (_stateGate)
        {
            if (_state is not (WorkerState.PreparingStorageMigrations or WorkerState.Stopping)
                || !string.Equals(envelope.ActivationId, _activationId.ToString("N"), StringComparison.Ordinal)
                || envelope.SessionGeneration != _sessionGeneration
                || _storageMigrationsPrepared.Task.IsCompleted)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker acknowledged invalid or duplicate storage-migration preparation.");
            }
            if (_state == WorkerState.PreparingStorageMigrations)
            {
                _state = WorkerState.StorageMigrationsPrepared;
            }
        }
        _storageMigrationsPrepared.TrySetResult();
    }

    private void HandleActivated(WorkerActivatedEnvelope envelope)
    {
        lock (_stateGate)
        {
            var validState = _state is WorkerState.Activating or WorkerState.Stopping;
            if (!validState
                || envelope.SessionGeneration != _sessionGeneration
                || _activationAcknowledged)
            {
                throw new SunderWorkerProtocolException("Process worker acknowledged an invalid Runtime generation.");
            }
            if (_state == WorkerState.Activating && !AreStorageMigrationsAppliedUnderStateGate())
            {
                throw new SunderWorkerProtocolException(
                    "Process worker acknowledged activation before completing its declared storage migrations.");
            }
            _activationAcknowledged = true;
            if (_state == WorkerState.Activating)
            {
                _state = WorkerState.Active;
            }
        }
        _activated.TrySetResult();
        _logger.LogInformation(
            "Worker V1 activation acknowledged for package {PackageId}, activation {ActivationId}, generation {SessionGeneration}",
            _package.PackageId,
            _activationId,
            envelope.SessionGeneration);
    }

    private void HandleGenerationFault(WorkerGenerationFaultEnvelope envelope)
    {
        var failure = new ProcessRuntimeGenerationFaultException(envelope.Code, envelope.Message);
        HostCall[] calls;
        WorkerContributionCall[] contributions;
        lock (_stateGate)
        {
            if (!(_state == WorkerState.Active || _state == WorkerState.Stopping
                    && (_shutdownAdmissionState == WorkerState.Active
                        || _shutdownAdmissionState == WorkerState.Activating && _activationAcknowledged))
                || _reportedGenerationFault is not null
                || _shutdownAcknowledgementReceived
                || !string.Equals(envelope.ActivationId, _activationId.ToString("N"), StringComparison.Ordinal)
                || envelope.SessionGeneration != _sessionGeneration)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker generation fault does not identify its exact active generation.");
            }
            _reportedGenerationFault = failure;
            _fault = failure;
            _shutdownAdmissionState = WorkerState.Active;
            _state = WorkerState.Stopping;
            lock (_callGate)
            {
                calls = _hostCalls.Values.ToArray();
                contributions = _contributionCalls.Values.ToArray();
            }
        }
        // Withdraw exact-generation authority before draining, while retaining the
        // control/logging transport needed to release scopes and package resources.
        _generationCompletion.TrySetException(failure);
        foreach (var call in calls) CancelHostCall(call, new CancellationToken(canceled: true));
        foreach (var call in contributions) CancelContributionCall(call, new CancellationToken(canceled: true));
        _ = ObserveAsync(StopAsync());
    }

    private bool CanDrainShutdown()
    {
        lock (_stateGate) return _state != WorkerState.Faulted && (_fault is null || _reportedGenerationFault is not null);
    }

    private void HandleShutdownAcknowledgement(WorkerShutdownAcknowledgementEnvelope envelope)
    {
        Task[] workerCallCompletions;
        lock (_stateGate)
        {
            if (_state != WorkerState.Stopping
                || _shutdownId is null
                || !string.Equals(_shutdownId, envelope.ShutdownId, StringComparison.Ordinal)
                || _shutdownAcknowledgementReceived)
            {
                throw new SunderWorkerProtocolException("Process worker sent an unsolicited or duplicate shutdown acknowledgement.");
            }
            _shutdownAcknowledgementReceived = true;
            lock (_callGate)
            {
                if (_hostCalls.Count != 0 || _contributionCalls.Count != 0)
                {
                    throw new SunderWorkerProtocolException(
                        "Process worker acknowledged shutdown before draining Host-owned calls.");
                }
                workerCallCompletions = _workerCallCleanup.ToArray();
            }
        }
        _candidateStarted.TrySetResult();
        _generationCommitted.TrySetResult();
        _storageMigrationsPrepared.TrySetResult();
        _activated.TrySetResult();
        _ = CompleteShutdownAcknowledgementAsync(workerCallCompletions);
    }

    private async Task CompleteShutdownAcknowledgementAsync(Task[] workerCallCompletions)
    {
        await Task.WhenAll(workerCallCompletions).ConfigureAwait(false);
        _shutdownAcknowledged.TrySetResult();
    }

    private void HandleHostCallResponse(string type, JsonElement root)
    {
        var allowed = type switch
        {
            "worker.result" or "worker.event" => new[] { "type", "id", "value" },
            "worker.complete" => new[] { "type", "id" },
            _ => new[] { "type", "id", "error" },
        };
        SunderWorkerProtocol.RequireOnlyProperties(root, allowed);
        var id = SunderWorkerProtocol.RequiredId(root);
        HostCall call;
        lock (_callGate)
        {
            if (!_hostCalls.TryGetValue(id, out call!))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent an unsolicited or duplicate response for call '{id}'.");
            }
            if (call.Terminal)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent a response after the terminal frame for call '{id}'.");
            }
            if (call.ProviderFaultDiagnosticPending)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker terminated call '{id}' before its provider-fault diagnostic was acknowledged.");
            }
            if (call.ProviderExceptionType is not null && type != "worker.error")
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker did not follow the provider-fault diagnostic for call '{id}' with worker.error.");
            }

            switch (type)
            {
                case "worker.event":
                    if (call.Kind != HostCallKind.ServerStream)
                    {
                        throw new SunderWorkerProtocolException("Process worker sent an event for a unary call.");
                    }
                    if (!call.Cancelled)
                    {
                        var value = SunderWorkerProtocol.RequiredValue(root, "value");
                        var queuedValue = SunderWorkerProtocol.CloneValueForQueue(
                            value,
                            _transportLimits,
                            out var valueBytes);
                        if (call.Events.TryWrite(queuedValue, valueBytes) is { } rejection)
                        {
                            call.Cancelled = true;
                            var exhausted = StreamQueueResourceExhausted(call, rejection);
                            call.UnaryCompletion.TrySetException(exhausted);
                            call.Events.Fail(exhausted);
                            TryQueueEnvelope(new { type = "host.cancel", id = call.Id });
                            _ = EnforceCancellationDrainAsync(call);
                        }
                    }
                    return;
                case "worker.result":
                    if (call.Kind != HostCallKind.Unary)
                    {
                        throw new SunderWorkerProtocolException("Process worker sent a unary result for a stream call.");
                    }
                    call.Terminal = true;
                    _hostCalls.Remove(id);
                    CleanupWorkerContentFiles(call);
                    if (!call.Cancelled)
                    {
                        call.UnaryCompletion.TrySetResult(
                            SunderWorkerProtocol.RequiredValue(root, "value").Clone());
                    }
                    return;
                case "worker.complete":
                    if (call.Kind != HostCallKind.ServerStream)
                    {
                        throw new SunderWorkerProtocolException("Process worker completed a unary call without a result.");
                    }
                    call.Terminal = true;
                    _hostCalls.Remove(id);
                    CleanupWorkerContentFiles(call);
                    if (!call.Cancelled) call.Events.Complete();
                    return;
                default:
                    var exception = call.Cancelled
                        ? null
                        : ReadWorkerError(SunderWorkerProtocol.RequiredValue(root, "error"), call);
                    call.Terminal = true;
                    _hostCalls.Remove(id);
                    CleanupWorkerContentFiles(call);
                    if (!call.Cancelled)
                    {
                        call.UnaryCompletion.TrySetException(exception!);
                        call.Events.Fail(exception!);
                    }
                    return;
            }
        }
    }

    private Exception ReadWorkerError(JsonElement value, HostCall call)
    {
        SunderWorkerProtocol.RequireOnlyProperties(value, "kind", "code", "message", "details");
        var kindText = SunderWorkerProtocol.RequiredString(value, "kind", 64);
        var code = SunderWorkerProtocol.RequiredString(value, "code", 128);
        if (!code.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new SunderWorkerProtocolException("Process worker returned an unsafe provider error code.");
        }
        var message = SunderWorkerProtocol.RequiredString(value, "message", 512);
        JsonElement? details = value.TryGetProperty("details", out var detailValue)
            ? detailValue.Clone()
            : null;
        if (kindText == "provider-fault")
        {
            if (details is not null)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker returned details on a provider-fault error.");
            }
            return new ProcessRpcProviderFaultException(
                code,
                call.ProviderExceptionType,
                call.ProviderExceptionFingerprint);
        }
        if (call.ProviderExceptionType is not null)
        {
            throw new SunderWorkerProtocolException(
                "Process worker returned a non-provider error after a provider-fault diagnostic.");
        }
        if (kindText == "operational")
        {
            if (!_operationalOutcomesV1)
                throw new SunderWorkerProtocolException("Process worker returned an operational outcome without negotiation.");
            if (details is not null)
                throw new SunderWorkerProtocolException("Process worker returned details on an operational outcome.");
            // Worker-controlled strings never become authenticated Host diagnostics. This closed
            // outcome only says the enclosing provider operation could not finish; it conveys no
            // permission, nested-call identity, or authority to replay an outer mutation.
            var (kind, hostCode, hostMessage) = code switch
            {
                "resource-exhausted" => (SunderRpcErrorKind.ResourceExhausted,
                    "rpc.provider.resource-exhausted", "The provider could not complete the RPC operation because resources are exhausted."),
                "unavailable" => (SunderRpcErrorKind.Unavailable,
                    "rpc.provider.unavailable", "The provider could not complete the RPC operation because a required service is unavailable."),
                "deadline-exceeded" => (SunderRpcErrorKind.DeadlineExceeded,
                    "rpc.provider.deadline-exceeded", "The provider could not complete the RPC operation before a required deadline."),
                _ => throw new SunderWorkerProtocolException("Process worker returned an unknown operational outcome."),
            };
            return SunderRpcException.Infrastructure(kind, hostCode, hostMessage)
                .WithDispatchMetadata(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe);
        }
        if (kindText == "resource-exhausted"
            && string.Equals(code, "rpc.worker.response-limit", StringComparison.Ordinal))
        {
            if (details is not null)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker returned details on a resource-exhausted error.");
            }
            return SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.response-limit",
                    "The RPC response exceeds the negotiated worker transport limits.")
                .WithDispatchMetadata(
                    SunderRpcDispatchState.PossiblyDispatched,
                    SunderRpcRetrySafety.Unsafe);
        }
        if (kindText != "domain")
        {
            throw new SunderWorkerProtocolException(
                $"Process worker attempted to return unauthenticated infrastructure error kind '{kindText}'.");
        }
        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            code,
            message,
            details: details));
    }

    private void BeginWorkerCall(string type, JsonElement root)
    {
        var id = SunderWorkerProtocol.RequiredId(root);
        var control = IsWorkerCleanupControl(type);
        var logging = IsWorkerLogging(type);
        var stream = type is "worker.watch" or "worker.subscribe";
        WorkerCall call;
        lock (_stateGate)
        {
            var admissionState = _state == WorkerState.Stopping && !_shutdownAcknowledgementReceived
                ? _shutdownAdmissionState
                : _state;
            var cancelForShutdown = _state == WorkerState.Stopping && !control && !logging;
            var admitsOrdinary = admissionState is WorkerState.Active or WorkerState.Activating;
            var admitsMigrationPreparationRead = admissionState == WorkerState.PreparingStorageMigrations
                                                   && IsWorkerMigrationPreparationStateRead(type);
            var admitsMigrationPreparationControl = admissionState == WorkerState.PreparingStorageMigrations
                                                      && type == "worker.payload-release";
            var admitsLogging = logging
                                && !_shutdownAcknowledgementReceived
                                && _state is WorkerState.Ready
                                    or WorkerState.CandidateStarting
                                    or WorkerState.CandidateStarted
                                    or WorkerState.Committing
                                    or WorkerState.Committed
                                    or WorkerState.PreparingStorageMigrations
                                    or WorkerState.StorageMigrationsPrepared
                                    or WorkerState.Activating
                                    or WorkerState.Active
                                    or WorkerState.Stopping;
            if (!(logging
                    ? admitsLogging
                    : admitsOrdinary
                      || admitsMigrationPreparationRead
                      || admitsMigrationPreparationControl
                      || _state == WorkerState.Stopping && control && !_shutdownAcknowledgementReceived))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent host call '{type}' before its Runtime generation was active.");
            }
            lock (_callGate)
            {
                if (_workerCalls.ContainsKey(id) || _rememberedWorkerIds.Contains(id))
                {
                    throw new SunderWorkerProtocolException($"Process worker reused duplicate message id '{id}'.");
                }
                var laneCount = _workerCalls.Values.Count(item =>
                    item.Control == control
                    && item.Logging == logging
                    && (!_operationalOutcomesV1 || item.Stream == stream));
                var laneLimit = control
                    ? _policy.MaxOutstandingWorkerControlCalls
                    : logging
                        ? _policy.MaxOutstandingWorkerLoggingCalls
                        : stream && _operationalOutcomesV1
                            ? _policy.MaxOutstandingWorkerStreams
                            : _policy.MaxOutstandingWorkerCalls;
                var parentFrame = ResolveWorkerCallParent(type, root);
                if (laneCount >= laneLimit)
                {
                    if (control || logging)
                        throw new SunderWorkerProtocolException(control
                            ? "Process worker exceeded its outstanding Host-control-call limit."
                            : "Process worker exceeded its outstanding logging-call limit.");
                    ValidateRejectedWorkerCall(type, root);
                    ReleaseRejectedWorkerPayload(type, root);
                    RememberWorkerId(id);
                    // Reject synchronously, without another running handler or an admission queue.
                    // The bounded protocol writer remains the final transport-pressure boundary.
                    QueueEnvelope(new
                    {
                        type = "host.error",
                        id,
                        error = WorkerWire.Error(new SunderRpcError(
                            SunderRpcErrorKind.ResourceExhausted,
                            stream ? "rpc.worker.stream-limit" : "rpc.worker.call-limit",
                            stream ? "The worker RPC observation limit was reached." : "The worker RPC call limit was reached.",
                            SunderRpcDispatchState.NotDispatched,
                            SunderRpcRetrySafety.Safe)),
                    });
                    return;
                }
                call = new WorkerCall(
                    id,
                    control,
                    logging,
                    stream,
                    parentFrame,
                    CancellationTokenSource.CreateLinkedTokenSource(_forceStop.Token));
                _workerCalls.Add(id, call);
                _workerCallCleanup.Add(call.Completion.Task);
                if (cancelForShutdown)
                {
                    call.HostCancelled = true;
                    call.HostCancelledBeforeDispatch = true;
                    call.CancellationCallbacks = RuntimeCancellation.Signal(call.Cancellation);
                }
            }
        }
        var request = root.Clone();
        _ = RunWorkerCallObservedAsync(type, request, call);
    }

    private async Task RunWorkerCallObservedAsync(string type, JsonElement root, WorkerCall call)
    {
        using var parentFrame = _broker.EnterCallFrame(call.ParentFrame);
        try
        {
            await RunWorkerCallAsync(type, root, call).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var dispatchState = call.HostCancelledBeforeDispatch
                ? SunderRpcDispatchState.NotDispatched
                : SunderRpcDispatchState.PossiblyDispatched;
            var retrySafety = call.HostCancelledBeforeDispatch
                ? SunderRpcRetrySafety.Safe
                : SunderRpcRetrySafety.Unsafe;
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    new SunderRpcError(
                        SunderRpcErrorKind.Cancelled,
                        "rpc.call.cancelled",
                        "The RPC call was cancelled.",
                        dispatchState,
                        retrySafety)),
            });
        }
        catch (SunderRpcException exception)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(exception.Error),
            });
        }
        catch (RuntimeUnavailableException)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    new SunderRpcError(
                        SunderRpcErrorKind.StaleEndpoint,
                        "rpc.caller.generation-stale",
                        "The package Runtime generation is no longer active.",
                        SunderRpcDispatchState.NotDispatched,
                        SunderRpcRetrySafety.Safe)),
            });
        }
        catch (ArgumentException) when (IsPackageDataCall(type))
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    new SunderRpcError(
                        SunderRpcErrorKind.Validation,
                        PackageDataValidationCode(type),
                        "The package data request violates its declared storage contract.")),
            });
        }
        catch (WorkerTransportResourceException)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    new SunderRpcError(
                        SunderRpcErrorKind.ResourceExhausted,
                        "rpc.worker.response-limit",
                        "The RPC response exceeds the negotiated worker transport limits.",
                        SunderRpcDispatchState.PossiblyDispatched,
                        SunderRpcRetrySafety.Unsafe)),
            });
        }
        catch (SunderWorkerProtocolException exception)
        {
            FaultProtocol(exception);
        }
        catch (Exception exception)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    new SunderRpcError(
                        SunderRpcErrorKind.Unavailable,
                        "rpc.host.adapter-fault",
                        "The Runtime RPC adapter could not complete the request.",
                        SunderRpcDispatchState.PossiblyDispatched,
                        SunderRpcRetrySafety.Unsafe)),
            });
            _logger.LogWarning(
                exception,
                "Worker-to-Host RPC adapter failed for process package {PackageId}",
                _package.PackageId);
        }
        finally
        {
            Task cancellationCallbacks;
            lock (_callGate)
            {
                _workerCalls.Remove(call.Id);
                // Live calls have their own bounded dictionary. Start the late
                // cancellation/duplicate-request history at completion, not
                // admission: a long-lived observation can outlast many finite
                // calls, but its terminal response may still cross cancellation.
                RememberWorkerId(call.Id);
                if (call.ClosesWorkerScope) _closingWorkerCallScopes--;
                if (call.ClosingScopeContentHandles > 0)
                {
                    _closingScopeContentHandles -= call.ClosingScopeContentHandles;
                }
                if (call.ReleasesContentHandle) _releasingMaterializedWorkerContentHandles--;
                if (call.ReleasesPayloadHandle) _releasingWorkerPayloadHandles--;
                cancellationCallbacks = call.CancellationCallbacks;
            }
            await cancellationCallbacks.ConfigureAwait(false);
            call.Cancellation.Dispose();
            lock (_callGate)
            {
                call.Completion.TrySetResult();
                _workerCallCleanup.Remove(call.Completion.Task);
            }
        }
    }

    private async Task RunWorkerCallAsync(string type, JsonElement root, WorkerCall call)
    {
        switch (type)
        {
            case "worker.get-provider":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(root, "type", "id", "endpointReference");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var provider = scope is null
                    ? await _broker.GetProviderAsync(
                        _callerStamp,
                        endpoint,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.GetProviderAsync(
                        endpoint,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new
                {
                    type = "host.result",
                    id = call.Id,
                    value = provider is null ? null : WorkerWire.ToWire(provider),
                });
                return;
            }
            case "worker.report-invariant-violation":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "exceptionMessage");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var exception = new InvalidOperationException(
                    SunderWorkerProtocol.RequiredString(root, "exceptionMessage", 512));
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var accepted = scope is null
                    ? await _broker.TryReportInvariantViolationAsync(
                        _callerStamp,
                        endpoint,
                        exception,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.TryReportInvariantViolationAsync(
                        endpoint,
                        exception,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = accepted });
                return;
            }
            case "worker.discover":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(root, "type", "id", "contractId");
                var contractId = SunderWorkerProtocol.RequiredString(root, "contractId", 256);
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var snapshot = scope is null
                    ? await _broker.DiscoverAsync(
                        _callerStamp,
                        contractId,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.DiscoverAsync(
                        contractId,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new
                {
                    type = "host.result",
                    id = call.Id,
                    value = WorkerWire.ToWire(snapshot),
                });
                return;
            }
            case "worker.watch":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(
                    root,
                    "type",
                    "id",
                    "afterRevision",
                    "afterSequence");
                var afterRevision = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterRevision");
                var afterSequence = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterSequence");
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var events = scope is null
                    ? _broker.WatchAsync(
                        _callerStamp,
                        afterRevision,
                        afterSequence,
                        call.Cancellation.Token)
                    : scope.Scope.WatchAsync(
                        afterRevision,
                        afterSequence,
                        call.Cancellation.Token);
                await foreach (var item in events.ConfigureAwait(false))
                {
                    QueueEnvelope(new
                    {
                        type = "host.event",
                        id = call.Id,
                        value = WorkerWire.ToWire(item),
                    });
                }
                QueueEnvelope(new { type = "host.complete", id = call.Id });
                return;
            }
            case "worker.invoke":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc",
                    "parentInvocationId");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var serviceId = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
                var methodId = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
                var request = SunderWorkerProtocol.RequiredValue(root, "request").Clone();
                var options = ReadCallOptions(root);
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var value = scope is null
                    ? await _broker.InvokeAsync(
                        _callerStamp,
                        endpoint,
                        serviceId,
                        methodId,
                        request,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.InvokeAsync(
                        endpoint,
                        serviceId,
                        methodId,
                        request,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value });
                return;
            }
            case "worker.subscribe":
            {
                var scopeId = ReadOptionalWorkerCallScopeId(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc",
                    "parentInvocationId");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var serviceId = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
                var methodId = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
                var request = SunderWorkerProtocol.RequiredValue(root, "request").Clone();
                var options = ReadCallOptions(root);
                call.Cancellation.Token.ThrowIfCancellationRequested();
                var scope = scopeId is null ? null : GetWorkerCallScope(scopeId);
                var events = scope is null
                    ? _broker.SubscribeAsync(
                        _callerStamp,
                        endpoint,
                        serviceId,
                        methodId,
                        request,
                        options,
                        call.Cancellation.Token)
                    : scope.Scope.SubscribeAsync(
                        endpoint,
                        serviceId,
                        methodId,
                        request,
                        options,
                        call.Cancellation.Token);
                await foreach (var item in events.ConfigureAwait(false))
                {
                    QueueEnvelope(new { type = "host.event", id = call.Id, value = item });
                }
                QueueEnvelope(new { type = "host.complete", id = call.Id });
                return;
            }
            case "worker.scope-open":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "deadlineUtc",
                    "parentInvocationId");
                var options = ReadCallOptions(root);
                call.Cancellation.Token.ThrowIfCancellationRequested();
                ISunderRpcCallScope? rpcScope = null;
                WorkerCallScope? workerScope = null;
                try
                {
                    rpcScope = await _broker.CreateCallScopeAsync(
                        _callerStamp,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false);
                    workerScope = AddWorkerCallScope(rpcScope);
                    rpcScope = null;
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    QueueEnvelope(new
                    {
                        type = "host.result",
                        id = call.Id,
                        value = new
                        {
                            scopeId = workerScope.Id,
                            deadlineUtc = workerScope.Scope.DeadlineUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        },
                    });
                }
                catch
                {
                    if (workerScope is not null)
                    {
                        await RemoveAndCloseWorkerCallScopeAsync(workerScope, call).ConfigureAwait(false);
                    }
                    else if (rpcScope is not null)
                    {
                        await rpcScope.DisposeAsync().ConfigureAwait(false);
                    }
                    throw;
                }
                return;
            }
            case "worker.scope-close":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "scopeId");
                WorkerCallScope scope;
                try
                {
                    scope = RemoveWorkerCallScope(ReadSafeId(root, "scopeId"), call);
                }
                catch (SunderWorkerProtocolException) when (IsStopping())
                {
                    QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                    return;
                }
                await CloseWorkerCallScopeAsync(scope).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return;
            }
            case "worker.content-register":
            {
                SunderRpcContentReference reference;
                var scoped = root.TryGetProperty("scopeId", out _);
                if (scoped)
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "scopeId",
                        "endpointReference",
                        "filePath",
                        "options");
                }
                else
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "invocationId",
                        "filePath",
                        "options");
                }
                var filePath = ResolveWorkerOwnedFilePath(
                    SunderWorkerProtocol.RequiredString(root, "filePath", 4096));
                var options = ReadContentRegistrationOptions(
                    SunderWorkerProtocol.RequiredValue(root, "options"));
                if (scoped)
                {
                    var scopeId = ReadSafeId(root, "scopeId");
                    var endpoint = new SunderRpcEndpointReference(
                        SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    var scope = GetWorkerCallScope(scopeId);
                    await using var source = OpenWorkerOwnedContentFileBounded(filePath);
                    reference = await scope.Scope.RegisterContentAsync(
                        endpoint,
                        source,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    var invocationId = ReadSafeId(root, "invocationId");
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    var hostCall = GetContentHostCall(invocationId);
                    await using var source = OpenWorkerOwnedContentFileBounded(filePath);
                    reference = await hostCall.Context.RegisterContentAsync(
                        source,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false);
                }
                QueueEnvelope(new { type = "host.result", id = call.Id, value = WorkerWire.ToWire(reference) });
                return;
            }
            case "worker.content-open":
            {
                var reference = ReadContentReference(SunderWorkerProtocol.RequiredValue(root, "reference"));
                if (root.TryGetProperty("scopeId", out _))
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "scopeId",
                        "reference");
                    var scopeId = ReadSafeId(root, "scopeId");
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    var scope = GetWorkerCallScope(scopeId);
                    ReserveMaterializedWorkerContentHandle();
                    var reservationHeld = true;
                    MaterializedWorkerContent? materialized = null;
                    try
                    {
                        await using var source = await scope.Scope.OpenContentAsync(
                            reference,
                            call.Cancellation.Token).ConfigureAwait(false);
                        materialized = await MaterializeWorkerContentAsync(
                            source,
                            reference,
                            call.Cancellation.Token).ConfigureAwait(false);
                        AddWorkerCallScopeContentFile(scope, materialized.HandleId, materialized.FilePath);
                        reservationHeld = false;
                        QueueEnvelope(new
                        {
                            type = "host.result",
                            id = call.Id,
                            value = new { handleId = materialized.HandleId, filePath = materialized.FilePath },
                        });
                    }
                    catch
                    {
                        if (materialized is not null) TryDeleteFile(materialized.FilePath);
                        throw;
                    }
                    finally
                    {
                        if (reservationHeld) ReleaseMaterializedWorkerContentHandleReservation();
                    }
                }
                else
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "invocationId",
                        "reference");
                    var invocationId = ReadSafeId(root, "invocationId");
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    var hostCall = GetContentHostCall(invocationId);
                    ReserveMaterializedWorkerContentHandle();
                    var reservationHeld = true;
                    MaterializedWorkerContent? materialized = null;
                    try
                    {
                        await using var source = await hostCall.Context.OpenContentAsync(
                            reference,
                            call.Cancellation.Token).ConfigureAwait(false);
                        materialized = await MaterializeWorkerContentAsync(
                            source,
                            reference,
                            call.Cancellation.Token).ConfigureAwait(false);
                        AddHostCallContentFile(
                            hostCall,
                            materialized.HandleId,
                            materialized.FilePath,
                            call.Cancellation.Token);
                        reservationHeld = false;
                        QueueEnvelope(new
                        {
                            type = "host.result",
                            id = call.Id,
                            value = new { handleId = materialized.HandleId, filePath = materialized.FilePath },
                        });
                    }
                    catch
                    {
                        if (materialized is not null) TryDeleteFile(materialized.FilePath);
                        throw;
                    }
                    finally
                    {
                        if (reservationHeld) ReleaseMaterializedWorkerContentHandleReservation();
                    }
                }
                return;
            }
            case "worker.content-release":
            {
                var handleId = ReadSafeId(root, "handleId");
                string filePath;
                if (root.TryGetProperty("scopeId", out _))
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "scopeId",
                        "handleId");
                    try
                    {
                        var scope = GetWorkerCallScope(ReadSafeId(root, "scopeId"));
                        lock (_callGate)
                        {
                            if (!scope.ContentFiles.Remove(handleId, out filePath!))
                            {
                                if (scope.Revoked)
                                {
                                    throw new SunderRpcException(new SunderRpcError(
                                        SunderRpcErrorKind.Unavailable,
                                        "rpc.scope.unavailable",
                                        "The RPC call scope is stale or unavailable."));
                                }
                                throw new SunderWorkerProtocolException(
                                    $"Process worker released unknown scoped content handle '{handleId}'.");
                            }
                            _releasingMaterializedWorkerContentHandles++;
                            call.ReleasesContentHandle = true;
                        }
                    }
                    catch (Exception exception) when (
                        IsStopping()
                        && exception is SunderWorkerProtocolException or SunderRpcException)
                    {
                        QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                        return;
                    }
                }
                else
                {
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "invocationId",
                        "handleId");
                    try
                    {
                        var hostCall = GetContentHostCall(root);
                        lock (_callGate)
                        {
                            if (!hostCall.ContentFiles.Remove(handleId, out filePath!))
                            {
                                throw new SunderWorkerProtocolException(
                                    $"Process worker released unknown invocation content handle '{handleId}'.");
                            }
                            _releasingMaterializedWorkerContentHandles++;
                            call.ReleasesContentHandle = true;
                        }
                    }
                    catch (SunderWorkerProtocolException) when (IsStopping())
                    {
                        QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                        return;
                    }
                }
                TryDeleteFile(filePath);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return;
            }
            default:
                if (await TryRunWorkerLoggingCallAsync(type, root, call).ConfigureAwait(false)) return;
                if (await TryRunPackageDataCallAsync(type, root, call).ConfigureAwait(false)) return;
                throw new SunderWorkerProtocolException($"Unknown worker Host-call type '{type}'.");
        }
    }

    private static string? ReadOptionalWorkerCallScopeId(
        JsonElement root,
        params string[] unscopedProperties)
    {
        if (!root.TryGetProperty("scopeId", out _))
        {
            SunderWorkerProtocol.RequireOnlyProperties(root, unscopedProperties);
            return null;
        }

        var scopedProperties = new string[unscopedProperties.Length + 1];
        unscopedProperties.CopyTo(scopedProperties, 0);
        scopedProperties[^1] = "scopeId";
        SunderWorkerProtocol.RequireOnlyProperties(root, scopedProperties);
        return ReadSafeId(root, "scopeId");
    }

    private WorkerCallScope AddWorkerCallScope(ISunderRpcCallScope scope)
    {
        if (scope is not RuntimeRpcCallScope runtimeScope)
        {
            throw new SunderWorkerProtocolException(
                "Runtime RPC broker returned an unsupported process call-scope implementation.");
        }
        var revocationToken = runtimeScope.RevocationToken;
        if (revocationToken.IsCancellationRequested)
        {
            throw DateTimeOffset.UtcNow >= scope.DeadlineUtc
                ? new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.DeadlineExceeded,
                    "rpc.call.deadline-exceeded",
                    "The RPC call deadline elapsed."))
                : new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.scope.unavailable",
                    "The RPC call scope is stale or unavailable."));
        }

        WorkerCallScope workerScope;
        lock (_callGate)
        {
            if (_workerCallScopes.Count + _closingWorkerCallScopes
                >= _policy.MaxOutstandingWorkerCallScopes)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.scope-limit",
                    "The process worker call-scope limit was reached."));
            }
            string id;
            do
            {
                id = "worker-scope-" + Guid.NewGuid().ToString("N");
            }
            while (_workerCallScopes.ContainsKey(id));
            workerScope = new WorkerCallScope(id, scope);
            _workerCallScopes.Add(id, workerScope);
        }

        try
        {
            var registration = revocationToken.UnsafeRegister(
                static state =>
                {
                    var (worker, registeredScope) = ((ProcessRuntimeWorker, WorkerCallScope))state!;
                    worker.HandleWorkerCallScopeRevocation(registeredScope);
                },
                (this, workerScope));
            workerScope.SetRevocationRegistration(registration);
            return workerScope;
        }
        catch
        {
            lock (_callGate)
            {
                if (_workerCallScopes.TryGetValue(workerScope.Id, out var registered)
                    && ReferenceEquals(registered, workerScope))
                {
                    _workerCallScopes.Remove(workerScope.Id);
                }
                workerScope.Closed = true;
            }
            throw;
        }
    }

    private WorkerCallScope GetWorkerCallScope(string scopeId)
    {
        lock (_callGate)
        {
            if (!_workerCallScopes.TryGetValue(scopeId, out var scope) || scope.Closed)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker used unknown RPC call-scope id '{scopeId}'.");
            }
            if (scope.Revoked)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.scope.unavailable",
                    "The RPC call scope is stale or unavailable."));
            }
            return scope;
        }
    }

    private WorkerCallScope RemoveWorkerCallScope(string scopeId, WorkerCall call)
    {
        lock (_callGate)
        {
            if (!_workerCallScopes.Remove(scopeId, out var scope) || scope.Closed)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker closed unknown or duplicate RPC call scope '{scopeId}'.");
            }
            ReserveClosingWorkerCallScopeUnderGate(scope, call);
            return scope;
        }
    }

    private async Task RemoveAndCloseWorkerCallScopeAsync(
        WorkerCallScope scope,
        WorkerCall call)
    {
        lock (_callGate)
        {
            if (_workerCallScopes.TryGetValue(scope.Id, out var registered)
                && ReferenceEquals(registered, scope))
            {
                _workerCallScopes.Remove(scope.Id);
            }
            ReserveClosingWorkerCallScopeUnderGate(scope, call);
        }
        await CloseWorkerCallScopeAsync(scope).ConfigureAwait(false);
    }

    private void ReserveClosingWorkerCallScopeUnderGate(
        WorkerCallScope scope,
        WorkerCall call)
    {
        scope.Closed = true;
        if (call.ClosesWorkerScope) return;
        call.ClosesWorkerScope = true;
        call.ClosingScopeContentHandles = scope.ContentFiles.Count;
        _closingWorkerCallScopes++;
        _closingScopeContentHandles += call.ClosingScopeContentHandles;
    }

    private async Task CloseWorkerCallScopeAsync(WorkerCallScope scope)
    {
        if (!scope.TryBeginClose()) return;
        scope.DisposeRevocationRegistration();
        string[] files;
        lock (_callGate)
        {
            scope.Closed = true;
            files = scope.ContentFiles.Values.ToArray();
            scope.ContentFiles.Clear();
        }
        foreach (var file in files) TryDeleteFile(file);
        await scope.Scope.DisposeAsync().ConfigureAwait(false);
    }

    private Task CloseAllWorkerCallScopesAsync()
    {
        WorkerCallScope[] scopes;
        lock (_callGate)
        {
            scopes = _workerCallScopes.Values.ToArray();
            _workerCallScopes.Clear();
            foreach (var scope in scopes) scope.Closed = true;
        }
        return Task.WhenAll(scopes.Select(CloseWorkerCallScopeAsync));
    }

    private void HandleWorkerCallScopeRevocation(WorkerCallScope scope)
    {
        string[] files;
        lock (_callGate)
        {
            if (scope.Closed
                || !_workerCallScopes.TryGetValue(scope.Id, out var registered)
                || !ReferenceEquals(registered, scope))
            {
                return;
            }
            scope.Revoked = true;
            files = scope.ContentFiles.Values.ToArray();
            scope.ContentFiles.Clear();
        }
        foreach (var file in files) TryDeleteFile(file);
    }

    private async ValueTask<MaterializedWorkerContent> MaterializeWorkerContentAsync(
        Stream source,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        if (reference.Length > _transportLimits.MaximumContentReferenceBytes)
        {
            throw ContentReferenceResourceExhausted();
        }
        var contentDirectory = PackageWorkspacePath.Resolve(
            _workerTemporaryPath,
            "rpc-content");
        Directory.CreateDirectory(contentDirectory);
        contentDirectory = PackageWorkspacePath.Resolve(_workerTemporaryPath, "rpc-content");
        var handleId = "worker-content-" + Guid.NewGuid().ToString("N");
        var filePath = Path.Combine(contentDirectory, handleId + ".content");
        try
        {
            await using (var destination = new FileStream(
                             filePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (new FileInfo(filePath).Length != reference.Length)
            {
                throw new SunderWorkerProtocolException(
                    "Host-mediated RPC content length changed while materializing it for a process worker.");
            }
            return new MaterializedWorkerContent(handleId, filePath);
        }
        catch
        {
            TryDeleteFile(filePath);
            throw;
        }
    }

    private void AddWorkerCallScopeContentFile(
        WorkerCallScope scope,
        string handleId,
        string filePath)
    {
        lock (_callGate)
        {
            if (scope.Closed
                || scope.Revoked
                || !_workerCallScopes.TryGetValue(scope.Id, out var registered)
                || !ReferenceEquals(registered, scope))
            {
                throw new OperationCanceledException("The process worker RPC call scope was revoked.");
            }
            _pendingMaterializedWorkerContentHandles--;
            scope.ContentFiles.Add(handleId, filePath);
        }
    }

    private void AddHostCallContentFile(
        HostCall hostCall,
        string handleId,
        string filePath,
        CancellationToken cancellationToken)
    {
        lock (_callGate)
        {
            if (!_hostCalls.TryGetValue(hostCall.Id, out var activeCall)
                || !ReferenceEquals(activeCall, hostCall)
                || hostCall.Terminal
                || hostCall.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            _pendingMaterializedWorkerContentHandles--;
            hostCall.ContentFiles.Add(handleId, filePath);
        }
    }

    private void ReserveMaterializedWorkerContentHandle()
    {
        lock (_callGate)
        {
            var count = _hostCalls.Values.Sum(static call => call.ContentFiles.Count)
                        + _workerCallScopes.Values.Sum(static scope => scope.ContentFiles.Count)
                        + _pendingMaterializedWorkerContentHandles
                        + _releasingMaterializedWorkerContentHandles
                        + _closingScopeContentHandles;
            if (count >= _policy.MaxMaterializedWorkerContentHandles)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.content-handle-limit",
                    "The process worker materialized content-handle limit was reached."));
            }
            _pendingMaterializedWorkerContentHandles++;
        }
    }

    private void ReleaseMaterializedWorkerContentHandleReservation()
    {
        lock (_callGate)
        {
            if (_pendingMaterializedWorkerContentHandles > 0)
            {
                _pendingMaterializedWorkerContentHandles--;
            }
        }
    }

    private HostCall GetContentHostCall(JsonElement root)
        => GetContentHostCall(ReadSafeId(root, "invocationId"));

    private HostCall GetContentHostCall(string invocationId)
    {
        lock (_callGate)
        {
            if (!_hostCalls.TryGetValue(invocationId, out var call))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker content operation does not identify an active Host invocation '{invocationId}'.");
            }
            if (call.Terminal || call.Cancelled)
            {
                throw new OperationCanceledException(call.Context.CancellationToken);
            }
            return call;
        }
    }

    private string ResolveWorkerOwnedFilePath(string value)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value, _package.ShadowFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SunderWorkerProtocolException("Process worker content file path is invalid.", exception);
        }
        var rootPath = IsWithinDirectory(fullPath, _package.ShadowFolder)
            ? _package.ShadowFolder
            : IsWithinDirectory(fullPath, _workerTemporaryPath)
                ? _workerTemporaryPath
                : null;
        if (rootPath is null)
        {
            throw new SunderWorkerProtocolException(
                "Process worker content files must be inside the package content or worker temporary directory.");
        }
        try
        {
            var relativePath = Path.GetRelativePath(rootPath, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return PackageWorkspacePath.Resolve(rootPath, relativePath);
        }
        catch (ArgumentException exception)
        {
            throw new SunderWorkerProtocolException(
                "Process worker content file traverses an invalid or linked path.",
                exception);
        }
    }

    private FileStream OpenWorkerOwnedContentFileBounded(string filePath)
    {
        var stream = OpenWorkerOwnedContentFile(filePath);
        if (stream.Length <= _transportLimits.MaximumContentReferenceBytes) return stream;
        stream.Dispose();
        throw ContentReferenceResourceExhausted();
    }

    private static FileStream OpenWorkerOwnedContentFile(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker content path must identify a regular file.");
            }
            var stream = OperatingSystem.IsWindows()
                ? new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan)
                : OpenUnixWorkerOwnedContentFile(filePath);
            var canSeek = stream.CanSeek;
            if (canSeek) return stream;
            stream.Dispose();
            throw new SunderWorkerProtocolException(
                "Process worker content path must identify a regular file.");
        }
        catch (SunderWorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SunderWorkerProtocolException(
                "Process worker content file could not be opened safely.",
                exception);
        }
    }

    private static FileStream OpenUnixWorkerOwnedContentFile(string filePath)
    {
        var flags = OperatingSystem.IsMacOS()
            ? 0x0004 | 0x0100 | 0x01000000
            : 0x0800 | 0x20000 | 0x80000;
        var descriptor = OpenUnixFile(filePath, flags);
        if (descriptor < 0)
        {
            throw new IOException(
                "The worker content file could not be opened.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixFile(string path, int flags);

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), path);
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private SunderRpcContentRegistrationOptions ReadContentRegistrationOptions(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "mediaType",
            "fileName",
            "length",
            "expiresAtUtc",
            "repeatability",
            "maximumUses");
        var lengthValue = SunderWorkerProtocol.RequiredValue(value, "length");
        long? length = lengthValue.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when SunderWorkerProtocol.IsCanonicalNonNegativeIntegerToken(lengthValue.GetRawText())
                                      && lengthValue.TryGetInt64(out var parsed)
                                      && parsed is >= 0 and <= SunderWorkerProtocol.MaximumSafeInteger => parsed,
            _ => throw new SunderWorkerProtocolException(
                "Process worker content registration length must be a non-negative integer or null."),
        };
        var expiryValue = SunderWorkerProtocol.RequiredValue(value, "expiresAtUtc");
        var expiresAtUtc = expiryValue.ValueKind switch
        {
            JsonValueKind.Null => (DateTimeOffset?)null,
            JsonValueKind.String => ReadUtcTimestamp(expiryValue.GetString()!, "expiresAtUtc"),
            _ => throw new SunderWorkerProtocolException(
                "Process worker content registration expiresAtUtc must be a UTC timestamp or null."),
        };
        var repeatability = SunderWorkerProtocol.RequiredString(value, "repeatability", 32) switch
        {
            "single-use" => SunderRpcContentRepeatability.SingleUse,
            "repeatable" => SunderRpcContentRepeatability.Repeatable,
            _ => throw new SunderWorkerProtocolException(
                "Process worker content registration repeatability is invalid."),
        };
        var maximumUses = SunderWorkerProtocol.RequiredNonNegativeInt64(value, "maximumUses");
        if (maximumUses is < 1 or > int.MaxValue)
        {
            throw new SunderWorkerProtocolException(
                "Process worker content registration maximumUses is invalid.");
        }
        if (length > _transportLimits.MaximumContentReferenceBytes)
        {
            throw ContentReferenceResourceExhausted();
        }
        return new SunderRpcContentRegistrationOptions(
            SunderWorkerProtocol.RequiredString(value, "mediaType", 128),
            SunderWorkerProtocol.RequiredString(value, "fileName", 255),
            length,
            expiresAtUtc,
            repeatability,
            (int)maximumUses);
    }

    private SunderRpcContentReference ReadContentReference(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "id",
            "length",
            "sha256",
            "mediaType",
            "fileName",
            "expiresAtUtc",
            "repeatability");
        var id = ReadSafeId(value, "id");
        var hash = SunderWorkerProtocol.RequiredString(value, "sha256", 64);
        if (hash.Length != 64 || !hash.All(static character => char.IsAsciiHexDigit(character)))
        {
            throw new SunderWorkerProtocolException("Process worker RPC content SHA-256 is invalid.");
        }
        var repeatability = SunderWorkerProtocol.RequiredString(value, "repeatability", 32) switch
        {
            "single-use" => SunderRpcContentRepeatability.SingleUse,
            "repeatable" => SunderRpcContentRepeatability.Repeatable,
            _ => throw new SunderWorkerProtocolException("Process worker RPC content repeatability is invalid."),
        };
        var length = SunderWorkerProtocol.RequiredNonNegativeInt64(value, "length");
        if (length > _transportLimits.MaximumContentReferenceBytes)
        {
            throw ContentReferenceResourceExhausted();
        }
        return new SunderRpcContentReference(
            id,
            length,
            hash,
            SunderWorkerProtocol.RequiredString(value, "mediaType", 128),
            SunderWorkerProtocol.RequiredString(value, "fileName", 255),
            ReadUtcTimestamp(
                SunderWorkerProtocol.RequiredString(value, "expiresAtUtc", 64),
                "expiresAtUtc"),
            repeatability);
    }

    private static DateTimeOffset ReadUtcTimestamp(string value, string propertyName)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result)
            || result.Offset != TimeSpan.Zero)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker content property '{propertyName}' must be a UTC timestamp.");
        }
        return result;
    }

    private static string ReadSafeId(JsonElement root, string propertyName)
    {
        var id = SunderWorkerProtocol.RequiredString(root, propertyName, 128);
        if (!id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new SunderWorkerProtocolException(
                $"Process worker content property '{propertyName}' contains unsupported characters.");
        }
        return id;
    }

    private static SunderRpcCallOptions? ReadCallOptions(JsonElement root)
    {
        if (!root.TryGetProperty("deadlineUtc", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                value.GetString(),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var deadline)
            || deadline.Offset != TimeSpan.Zero)
        {
            throw new SunderWorkerProtocolException("Worker RPC deadlineUtc must be a round-trip UTC timestamp or null.");
        }
        return new SunderRpcCallOptions(deadline);
    }

    private RuntimeRpcCallFrame? ResolveWorkerCallParent(string type, JsonElement root)
    {
        if (type is not ("worker.invoke" or "worker.subscribe" or "worker.scope-open")) return null;

        var hasParentProperty = root.TryGetProperty("parentInvocationId", out var parentValue);
        if (_rpcExtendedDeadlineV1 != hasParentProperty)
        {
            throw new SunderWorkerProtocolException(_rpcExtendedDeadlineV1
                ? "Worker RPC request omitted parentInvocationId required by rpc.extended-deadline.v1."
                : "Worker RPC request used parentInvocationId without declaring rpc.extended-deadline.v1.");
        }
        RuntimeRpcCallFrame? declaredParent = null;
        if (hasParentProperty && parentValue.ValueKind != JsonValueKind.Null)
        {
            if (parentValue.ValueKind != JsonValueKind.String)
            {
                throw new SunderWorkerProtocolException(
                    "Worker RPC parentInvocationId must be a live Host invocation id or null.");
            }
            var parentId = ReadSafeId(root, "parentInvocationId");
            if (!_hostCalls.TryGetValue(parentId, out var parentCall))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker used unknown or completed parent invocation id '{parentId}'.");
            }
            declaredParent = new(parentCall.Context.CallDepth, parentCall.Context.DeadlineUtc);
        }

        RuntimeRpcCallFrame? frame = null;
        foreach (var hostCall in _hostCalls.Values)
        {
            var context = hostCall.Context;
            frame = frame is null
                ? new RuntimeRpcCallFrame(context.CallDepth, context.DeadlineUtc)
                : new RuntimeRpcCallFrame(
                    Math.Max(frame.Depth, context.CallDepth),
                    frame.DeadlineUtc < context.DeadlineUtc
                        ? frame.DeadlineUtc
                        : context.DeadlineUtc);
        }
        if (!_rpcCallOwnershipV1 || frame is null) return frame;
        // This manifest capability permits independently bounded root work. A named
        // parent still caps its children; unrelated calls do not supply a deadline.
        // Retain the process-wide depth floor so concurrent/reentrant roots cannot
        // evade the Host's recursion limit by selecting a shallower or null parent.
        return new RuntimeRpcCallFrame(frame.Depth, declaredParent?.DeadlineUtc ?? DateTimeOffset.MaxValue);
    }

    private void HandleWorkerCancellation(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id");
        var id = SunderWorkerProtocol.RequiredId(root);
        lock (_callGate)
        {
            if (!_workerCalls.TryGetValue(id, out var call))
            {
                // A terminal Host response and worker cancellation can cross on independent streams.
                if (_rememberedWorkerIds.Contains(id)) return;
                throw new SunderWorkerProtocolException(
                    $"Process worker sent an unsolicited cancellation for '{id}'.");
            }
            if (call.Cancelled)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent a duplicate cancellation for '{id}'.");
            }
            call.Cancelled = true;
            if (call.HostCancelled) return;
            call.CancellationCallbacks = RuntimeCancellation.Signal(call.Cancellation);
        }
    }

    private void CancelWorkerCallsForShutdown()
    {
        WorkerCall[] calls;
        lock (_callGate)
        {
            calls = _workerCalls.Values
                .Where(static call => !call.Control && !call.Logging && !call.Cancelled && !call.HostCancelled)
                .ToArray();
            foreach (var call in calls)
            {
                call.HostCancelled = true;
                call.CancellationCallbacks = RuntimeCancellation.Signal(call.Cancellation);
            }
        }
    }

    private static bool IsWorkerCleanupControl(string type)
        => type is "worker.scope-close"
            or "worker.content-release"
            or "worker.payload-release";

    private static bool IsWorkerLogging(string type)
        => type is "worker.logging-write" or "worker.provider-fault-diagnostic";

    private static bool IsWorkerMigrationPreparationStateRead(string type)
        => type is "worker.state-get" or "worker.state-list";

    private static string PackageDataValidationCode(string type)
        => type.StartsWith("worker.migration-", StringComparison.Ordinal)
            ? "worker.migration.validation"
            : type.StartsWith("worker.settings-", StringComparison.Ordinal)
            ? "worker.settings.validation"
            : type.StartsWith("worker.secrets-", StringComparison.Ordinal)
                ? "worker.secrets.validation"
                : type.StartsWith("worker.files-", StringComparison.Ordinal)
                    ? "worker.files.validation"
                    : "worker.state.validation";

    private void RememberWorkerId(string id)
    {
        _rememberedWorkerIds.Add(id);
        _rememberedWorkerIdOrder.Enqueue(id);
        while (_rememberedWorkerIdOrder.Count > _policy.MaxRememberedMessageIds)
        {
            _rememberedWorkerIds.Remove(_rememberedWorkerIdOrder.Dequeue());
        }
    }

    private async Task WriteLoopAsync(Stream stdin)
    {
        try
        {
            await foreach (var body in _writes!.Reader.ReadAllAsync(_forceStop.Token).ConfigureAwait(false))
            {
                ReleaseWriteQueueBytes(body.Length);
                await SunderWorkerProtocol.WriteFrameBodyAsync(stdin, body, _forceStop.Token).ConfigureAwait(false);
                if (ReferenceEquals(body, Volatile.Read(ref _activationFrameBody)))
                {
                    _activationDispatched.TrySetResult();
                    _logger.LogInformation(
                        "Worker V1 activation dispatched for package {PackageId}, activation {ActivationId}, generation {SessionGeneration}",
                        _package.PackageId,
                        _activationId,
                        _sessionGeneration);
                }
            }
        }
        catch (OperationCanceledException) when (_forceStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsStopping()) Fault(new SunderWorkerProtocolException("Process worker protocol writer failed.", exception));
        }
        finally
        {
            if (_writes is not null)
            {
                while (_writes.Reader.TryRead(out var body)) ReleaseWriteQueueBytes(body.Length);
            }
        }
    }

    private async Task DrainStderrAsync(Stream stderr)
    {
        var buffer = new byte[4096];
        long receivedBytes = 0;
        var limitReported = false;
        try
        {
            while (true)
            {
                var read = await stderr.ReadAsync(buffer, _forceStop.Token).ConfigureAwait(false);
                if (read == 0) break;
                receivedBytes += read;
                if (receivedBytes > _policy.MaxStderrBytes && !limitReported)
                {
                    limitReported = true;
                    _logger.LogWarning(
                        "Process Runtime stderr for package {PackageId} exceeded {MaximumBytes} bytes; raw stderr is discarded",
                        _package.PackageId,
                        _policy.MaxStderrBytes);
                }
            }
        }
        catch (OperationCanceledException) when (_forceStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to drain stderr for process package {PackageId}", _package.PackageId);
        }
    }

    private async Task MonitorExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (!IsStopping())
            {
                int? exitCode = null;
                try
                {
                    exitCode = process.ExitCode;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // An attached Unix process has no wait handle from which .NET can recover its exit code.
                }
                Fault(new ProcessRuntimeWorkerException(
                    exitCode is null
                        ? $"Process Runtime target for package '{_package.PackageId}' exited unexpectedly."
                        : $"Process Runtime target for package '{_package.PackageId}' exited unexpectedly with code {exitCode}."));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            if (!IsStopping())
            {
                Fault(new ProcessRuntimeWorkerException(
                    $"Process Runtime target for package '{_package.PackageId}' exited unexpectedly.",
                    exception));
            }
        }
        catch (Exception exception)
        {
            if (!IsStopping()) Fault(exception);
        }
    }

    private void QueueEnvelope(object envelope)
    {
        var body = SunderWorkerProtocol.SerializeFrameBody(envelope, _transportLimits);
        if (!TryQueueFrameBody(body))
        {
            throw WriteQueueFailure(body.Length);
        }
    }

    private WorkerTransportResourceException WriteQueueFailure(int incomingBytes)
    {
        var writes = _writes;
        if (writes is null || writes.Reader.Completion.IsCompleted || IsStopping())
            return new(WorkerTransportFailureKind.WriteQueueClosed, "Process worker protocol writer is unavailable or retiring.");
        lock (_writeQueueGate)
        {
            if (incomingBytes > _transportLimits.MaximumWriteQueueBytes - _queuedWriteBytes)
                return new(WorkerTransportFailureKind.WriteQueueAdmission, "Process worker protocol write queue exceeded its byte bound.",
                    _queuedWriteBytes + incomingBytes, _transportLimits.MaximumWriteQueueBytes);
        }
        return new(WorkerTransportFailureKind.WriteQueueAdmission, "Process worker protocol write queue rejected a frame at its message bound.",
            writes.Reader.Count + 1L, _policy.MaxWriteQueueMessages);
    }

    private void QueueHostCallBody(byte[] body)
    {
        if (TryQueueFrameBody(body)) return;
        throw new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.write-queue-limit",
            "The process worker write queue is full.",
            SunderRpcDispatchState.NotDispatched,
            SunderRpcRetrySafety.Safe));
    }

    private bool TryQueueFrameBody(byte[] body)
    {
        var writes = _writes;
        if (writes is null || !TryReserveWriteQueueBytes(body.Length)) return false;
        if (writes.Writer.TryWrite(body)) return true;
        ReleaseWriteQueueBytes(body.Length);
        return false;
    }

    private bool TryReserveWriteQueueBytes(int bodyBytes)
    {
        lock (_writeQueueGate)
        {
            if (bodyBytes > _transportLimits.MaximumWriteQueueBytes - _queuedWriteBytes) return false;
            _queuedWriteBytes += bodyBytes;
            return true;
        }
    }

    private void ReleaseWriteQueueBytes(int bodyBytes)
    {
        lock (_writeQueueGate)
        {
            _queuedWriteBytes -= bodyBytes;
            if (_queuedWriteBytes < 0)
            {
                _queuedWriteBytes = 0;
                throw new InvalidOperationException("Process worker write-queue byte accounting underflowed.");
            }
        }
    }

    private void TryQueueEnvelope(object envelope)
    {
        try
        {
            QueueEnvelope(envelope);
        }
        catch (Exception exception)
        {
            _ = ObserveAsync(Task.Run(() => Fault(exception)));
        }
    }

    private void FaultProtocol(SunderWorkerProtocolException exception)
        => Fault(exception, faultDuringStopping: true);

    private void Fault(Exception exception, bool faultDuringStopping = false)
    {
        lock (_stateGate)
        {
            if (_state is WorkerState.Stopped or WorkerState.Faulted
                || _state == WorkerState.Stopping && !faultDuringStopping)
            {
                return;
            }
            _state = WorkerState.Faulted;
            _fault = exception;
        }
        _ready.TrySetException(exception);
        _candidateStarted.TrySetResult();
        _generationCommitted.TrySetResult();
        _storageMigrationsPrepared.TrySetResult();
        if (Volatile.Read(ref _activationFrameBody) is not null)
        {
            _activationDispatched.TrySetException(exception);
        }
        _activated.TrySetException(exception);
        _generationCompletion.TrySetException(exception);
        _shutdownAcknowledged.TrySetException(exception);
        FailOutstandingCalls(exception);
        FailOutstandingContributionCalls(exception);
        _ = ObserveAsync(CloseAllWorkerCallScopesAsync());
        _writes?.Writer.TryComplete(exception);
        _ = SignalForceStop();
        if (_containedProcess is not null)
        {
            _ = ObserveAsync(TerminateContainedProcessAsync());
        }
    }

    private void FailOutstandingCalls(Exception exception)
    {
        HostCall[] hostCalls;
        WorkerCall[] workerCalls;
        lock (_callGate)
        {
            hostCalls = _hostCalls.Values.ToArray();
            _hostCalls.Clear();
            workerCalls = _workerCalls.Values.ToArray();
            foreach (var call in workerCalls)
            {
                if (!call.Cancelled && !call.HostCancelled)
                {
                    call.HostCancelled = true;
                    call.CancellationCallbacks = RuntimeCancellation.Signal(call.Cancellation);
                }
            }
        }
        foreach (var call in hostCalls)
        {
            call.Terminal = true;
            CleanupWorkerContentFiles(call);
            call.UnaryCompletion.TrySetException(exception);
            call.Events.Fail(exception);
        }
    }

    private Task SignalForceStop()
    {
        lock (_forceStopGate)
        {
            return _forceStopCallbacks ??= RuntimeCancellation.Signal(_forceStop);
        }
    }

    private async Task TerminateAfterFailureAsync()
    {
        var exception = _fault ?? new ProcessRuntimeWorkerException(
            $"Process Runtime target for package '{_package.PackageId}' failed during startup.");
        Fault(exception);
        try
        {
            if (_containedProcess is not null)
            {
                await TerminateContainedProcessAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await ReleaseExecutionRootsAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseExecutionRootsAsync()
    {
        await _runtimeLease.ReleaseAfterProcessTreeTerminationAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var projection = Volatile.Read(ref _packageProjection);
        if (projection is not null)
        {
            await projection.DisposeAsync().ConfigureAwait(false);
            Interlocked.CompareExchange(ref _packageProjection, null, projection);
        }
    }

    private async Task StopFromHostAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop process package {PackageId} during Host shutdown", _package.PackageId);
        }
    }

    private void CompleteExpectedStop()
    {
        FailOutstandingCalls(_fault ?? new OperationCanceledException("The process worker activation stopped."));
        FailOutstandingContributionCalls(new OperationCanceledException(
            "The process worker activation stopped."));
        lock (_stateGate)
        {
            _state = WorkerState.Stopped;
        }
        _candidateStarted.TrySetResult();
        _generationCommitted.TrySetResult();
        _storageMigrationsPrepared.TrySetResult();
        _activated.TrySetResult();
        if (_fault is null) _generationCompletion.TrySetResult();
        _stopped.TrySetResult();
    }

    private void ResetFailedStop(Exception exception)
    {
        TaskCompletionSource failedStop;
        lock (_stateGate)
        {
            failedStop = _stopped;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopStarted = false;
        }
        failedStop.TrySetException(exception);
    }

    private void CleanupAllWorkerContentFiles()
    {
        lock (_callGate)
        {
            foreach (var call in _hostCalls.Values)
            {
                CleanupWorkerContentFiles(call);
            }
        }
    }

    private static void CleanupWorkerContentFiles(HostCall call)
    {
        foreach (var filePath in call.ContentFiles.Values)
        {
            TryDeleteFile(filePath);
        }
        call.ContentFiles.Clear();
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private bool IsStopping()
    {
        lock (_stateGate)
        {
            return _state is WorkerState.Stopping or WorkerState.Stopped or WorkerState.Faulted;
        }
    }

    private void ThrowIfFaulted()
    {
        if (_fault is not null)
        {
            throw new InvalidOperationException(
                $"Process Runtime target for package '{_package.PackageId}' faulted.",
                _fault);
        }
    }

    private Task TerminateContainedProcessAsync()
    {
        var containedProcess = _containedProcess;
        return containedProcess is null
            ? Task.CompletedTask
            : containedProcess.TerminateAndDrainAsync(CancellationToken.None);
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

    private static RuntimeProcessPolicyOptions ValidatePolicy(RuntimeProcessPolicyOptions policy)
    {
        if (policy.MaxHeaderBytes <= 0
            || policy.MaxOutstandingHostCalls <= 0
            || policy.MaxOutstandingHostStreams <= 0
            || policy.MaxOutstandingWorkerCalls <= 0
            || policy.MaxOutstandingWorkerStreams <= 0
            || policy.MaxOutstandingWorkerLoggingCalls <= 0
            || policy.MaxOutstandingWorkerCallScopes <= 0
            || policy.MaxMaterializedWorkerContentHandles <= 0
            || policy.MaxWorkerPayloadHandles <= 0
            || policy.MaxWorkerPayloadBytes <= 0
            || policy.MaxWorkerFilePayloadBytes < policy.MaxWorkerPayloadBytes
            || policy.MaxOutstandingWorkerControlCalls
            < policy.MaxOutstandingWorkerCallScopes
              + policy.MaxMaterializedWorkerContentHandles
              + policy.MaxWorkerPayloadHandles
            || policy.MaxWriteQueueMessages <= 0
            || policy.MaxRememberedMessageIds <= 0
            || policy.MaxStderrBytes <= 0
            || policy.MaxStderrLineCharacters <= 0
            || policy.StartupTimeout <= TimeSpan.Zero
            || policy.ShutdownTimeout <= TimeSpan.Zero
            || policy.CancellationDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Process worker limits and deadlines must be positive.");
        }
        return policy;
    }

    private static RuntimeResourcePolicy ResolveEffectiveResourcePolicy(RuntimeProcessPolicyOptions policy)
    {
        if (policy.EffectiveResourcePolicy is not null) return policy.EffectiveResourcePolicy;
        var launchPayload = Environment.GetEnvironmentVariable(
            RuntimeResourcePolicyEnvironment.LaunchPayloadVariable);
        if (string.IsNullOrWhiteSpace(launchPayload)) return RuntimeResourcePolicies.Standard;
        try
        {
            return RuntimeResourcePolicyContract.ParseLaunchPayload(
                Encoding.UTF8.GetBytes(launchPayload)).EffectivePolicy;
        }
        catch (InvalidDataException exception)
        {
            throw new ProcessRuntimeWorkerException(
                "The Runtime resource-policy launch payload is invalid.",
                exception);
        }
    }

    private static SunderRpcException HostRequestResourceExhausted(Exception? _ = null)
        => new(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.request-limit",
            "The RPC request exceeds the negotiated worker transport limits.",
            SunderRpcDispatchState.NotDispatched,
            SunderRpcRetrySafety.Safe));

    private static SunderRpcException StreamQueueResourceExhausted(
        HostCall call,
        HostEventQueueRejection rejection)
    {
        var message = FormattableString.Invariant(
            $"The process worker stream queue exceeded its count or byte bound. Local diagnostic: limit={FormatQueueLimit(rejection.Limit)}; queuedMessages={rejection.QueuedMessages}/{rejection.MaximumMessages}; queuedBytes={rejection.QueuedBytes}/{rejection.MaximumBytes}; incomingBytes={rejection.IncomingBytes}; provider={SanitizeQueueIdentity(call.ProviderId)}; service={SanitizeQueueIdentity(call.ServiceId)}; method={SanitizeQueueIdentity(call.MethodId)}.");
        if (message.Length > 512) message = message[..512];
        return SunderRpcException.Infrastructure(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.worker.stream-queue-limit",
                message)
            .WithDispatchMetadata(
                SunderRpcDispatchState.PossiblyDispatched,
                SunderRpcRetrySafety.Unsafe);
    }

    private static string FormatQueueLimit(HostEventQueueLimit limit)
        => limit switch
        {
            HostEventQueueLimit.Count => "count",
            HostEventQueueLimit.Bytes => "bytes",
            HostEventQueueLimit.Count | HostEventQueueLimit.Bytes => "count+bytes",
            _ => "unknown",
        };

    private static string SanitizeQueueIdentity(string value)
    {
        var characters = new char[Math.Min(value.Length, 64)];
        for (var index = 0; index < characters.Length; index++)
        {
            var character = value[index];
            characters[index] = char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_';
        }
        return new string(characters);
    }

    private static SunderRpcException ContentReferenceResourceExhausted()
        => new(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.content-reference-limit",
            "The RPC content reference exceeds the negotiated worker byte limit.",
            SunderRpcDispatchState.NotDispatched,
            SunderRpcRetrySafety.Safe));

    private enum WorkerState
    {
        Created,
        Starting,
        Ready,
        CandidateStarting,
        CandidateStarted,
        Committing,
        Committed,
        PreparingStorageMigrations,
        StorageMigrationsPrepared,
        Activating,
        Active,
        Stopping,
        Stopped,
        Faulted,
    }

    private enum HostCallKind
    {
        Unary,
        ServerStream,
    }

    private sealed class HostCall(
        string id,
        HostCallKind kind,
        SunderRpcInvocationContext context,
        string providerId,
        string serviceId,
        string methodId,
        long streamQueueBytes)
    {
        public string Id { get; } = id;
        public HostCallKind Kind { get; } = kind;
        public SunderRpcInvocationContext Context { get; } = context;
        public string ProviderId { get; } = providerId;
        public string ServiceId { get; } = serviceId;
        public string MethodId { get; } = methodId;
        public Dictionary<string, string> ContentFiles { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<JsonElement> UnaryCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BoundedHostEventQueue Events { get; } = new(StreamBufferCapacity, streamQueueBytes);
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }
        public bool InvocationQueued { get; set; }
        public bool ProviderFaultDiagnosticPending { get; set; }
        public string? ProviderExceptionType { get; set; }
        public string? ProviderExceptionFingerprint { get; set; }
    }

    private sealed class BoundedHostEventQueue
    {
        private readonly Channel<QueuedHostEvent> _channel;
        private readonly int _maximumMessages;
        private readonly long _maximumBytes;
        private readonly object _gate = new();
        private int _queuedMessages;
        private long _queuedBytes;

        public BoundedHostEventQueue(int maximumMessages, long maximumBytes)
        {
            _maximumMessages = maximumMessages;
            _maximumBytes = maximumBytes;
            _channel = Channel.CreateBounded<QueuedHostEvent>(new BoundedChannelOptions(maximumMessages)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        public HostEventQueueRejection? TryWrite(JsonElement value, int bytes)
        {
            lock (_gate)
            {
                var exceededLimit = HostEventQueueLimit.None;
                if (_queuedMessages >= _maximumMessages) exceededLimit |= HostEventQueueLimit.Count;
                if (bytes > _maximumBytes - _queuedBytes) exceededLimit |= HostEventQueueLimit.Bytes;
                if (exceededLimit != HostEventQueueLimit.None)
                {
                    return new HostEventQueueRejection(
                        exceededLimit,
                        _queuedMessages,
                        _queuedBytes,
                        _maximumMessages,
                        _maximumBytes,
                        bytes);
                }
                _queuedMessages++;
                _queuedBytes += bytes;
            }
            if (_channel.Writer.TryWrite(new QueuedHostEvent(value, bytes))) return null;
            Release(bytes);
            throw new InvalidOperationException("Host event queue rejected an admitted value.");
        }

        public async IAsyncEnumerable<JsonElement> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Release(item.Bytes);
                yield return item.Value;
            }
        }

        public void Complete() => _channel.Writer.TryComplete();

        public void Fail(Exception exception)
        {
            _channel.Writer.TryComplete(exception);
            while (_channel.Reader.TryRead(out var item)) Release(item.Bytes);
        }

        private void Release(int bytes)
        {
            lock (_gate)
            {
                _queuedMessages--;
                _queuedBytes -= bytes;
                if (_queuedMessages < 0 || _queuedBytes < 0)
                {
                    throw new InvalidOperationException("Host event-queue accounting underflowed.");
                }
            }
        }

        private sealed record QueuedHostEvent(JsonElement Value, int Bytes);
    }

    [Flags]
    private enum HostEventQueueLimit
    {
        None = 0,
        Count = 1,
        Bytes = 2,
    }

    private readonly record struct HostEventQueueRejection(
        HostEventQueueLimit Limit,
        int QueuedMessages,
        long QueuedBytes,
        int MaximumMessages,
        long MaximumBytes,
        int IncomingBytes);

    private sealed class WorkerCallScope(string id, ISunderRpcCallScope scope)
    {
        private readonly object _registrationGate = new();
        private CancellationTokenRegistration _revocationRegistration;
        private bool _hasRevocationRegistration;
        private int _closing;

        public string Id { get; } = id;
        public ISunderRpcCallScope Scope { get; } = scope;
        public Dictionary<string, string> ContentFiles { get; } = new(StringComparer.Ordinal);
        public bool Revoked { get; set; }
        public bool Closed { get; set; }

        public bool TryBeginClose() => Interlocked.Exchange(ref _closing, 1) == 0;

        public void SetRevocationRegistration(CancellationTokenRegistration registration)
        {
            var dispose = false;
            lock (_registrationGate)
            {
                if (Volatile.Read(ref _closing) != 0)
                {
                    dispose = true;
                }
                else
                {
                    _revocationRegistration = registration;
                    _hasRevocationRegistration = true;
                }
            }
            if (dispose) registration.Dispose();
        }

        public void DisposeRevocationRegistration()
        {
            CancellationTokenRegistration registration = default;
            var dispose = false;
            lock (_registrationGate)
            {
                if (_hasRevocationRegistration)
                {
                    registration = _revocationRegistration;
                    _hasRevocationRegistration = false;
                    dispose = true;
                }
            }
            if (dispose) registration.Dispose();
        }
    }

    private sealed class WorkerCall(
        string id,
        bool control,
        bool logging,
        bool stream,
        RuntimeRpcCallFrame? parentFrame,
        CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public bool Control { get; } = control;
        public bool Logging { get; } = logging;
        public bool Stream { get; } = stream;
        public RuntimeRpcCallFrame? ParentFrame { get; } = parentFrame;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task CancellationCallbacks { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; set; }
        public bool HostCancelled { get; set; }
        public bool HostCancelledBeforeDispatch { get; set; }
        public bool ClosesWorkerScope { get; set; }
        public int ClosingScopeContentHandles { get; set; }
        public bool ReleasesContentHandle { get; set; }
        public bool ReleasesPayloadHandle { get; set; }
    }

    private sealed record MaterializedWorkerContent(string HandleId, string FilePath);

    private sealed record SerializedHostInvocation(string Id, byte[] Body);

    private sealed record ProviderIdentity(
        string ProviderId,
        string ContractId,
        string ContractVersion,
        string DescriptorSha256,
        string AbiSha256)
    {
        public object ToWire()
            => new
            {
                providerId = ProviderId,
                contractId = ContractId,
                contractVersion = ContractVersion,
                descriptorSha256 = DescriptorSha256,
                abiSha256 = AbiSha256,
            };
    }
}

internal sealed class ProcessRpcProviderFaultException : Exception
{
    public ProcessRpcProviderFaultException(
        string code,
        string? providerExceptionType = null,
        string? providerExceptionFingerprint = null)
        : base($"Process RPC provider reported fault '{code}'.")
    {
        ProviderExceptionType = providerExceptionType;
        ProviderExceptionFingerprint = providerExceptionFingerprint;
    }

    public string? ProviderExceptionType { get; }

    public string? ProviderExceptionFingerprint { get; }
}

internal sealed class ProcessRuntimeGenerationFaultException : ProcessRuntimeWorkerException
{
    public ProcessRuntimeGenerationFaultException(string code, string message)
        : base($"Worker reported generation fault '{code}': {message}")
        => Code = code;

    public string Code { get; }
}

internal sealed class ProcessRpcProviderHandler(
    ProcessRuntimeWorker worker,
    string providerId) : ISunderRpcServiceHandler
{
    public ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => worker.InvokeUnaryAsync(providerId, context, serviceId, methodId, request, cancellationToken);

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => worker.InvokeServerStreamAsync(providerId, context, serviceId, methodId, request, cancellationToken);
}

internal static class WorkerWire
{
    public static object ToWire(SunderRpcProviderSnapshot provider)
    {
        var state = provider.State switch
        {
            SunderRpcProviderState.Active => "active",
            SunderRpcProviderState.Inactive => "inactive",
            _ => "faulted",
        };
        return new
        {
            packageId = provider.PackageId,
            packageVersion = provider.PackageVersion,
            providerId = provider.ProviderId,
            contractId = provider.ContractId,
            contractVersion = provider.ContractVersion,
            descriptorSha256 = provider.DescriptorSha256,
            abiSha256 = provider.AbiSha256
                        ?? throw new SunderWorkerProtocolException(
                            "Runtime provider snapshot is missing its ABI identity."),
            activationId = provider.ActivationId.ToString("N"),
            activationEpoch = provider.ActivationEpoch,
            sessionGeneration = provider.SessionGeneration,
            endpointReference = provider.Endpoint.Value,
            catalogRevision = provider.CatalogRevision,
            state,
            faultCode = provider.FaultCode,
        };
    }

    public static object ToWire(SunderRpcCatalogSnapshot snapshot)
        => new
        {
            revision = snapshot.Revision,
            sequence = snapshot.Sequence,
            providers = snapshot.Providers.Select(ToWire).ToArray(),
            resetRequired = snapshot.ResetRequired,
        };

    public static object ToWire(SunderRpcCatalogEvent value)
        => new
        {
            revision = value.Revision,
            sequence = value.Sequence,
            kind = value.Kind switch
            {
                SunderRpcCatalogEventKind.Added => "added",
                SunderRpcCatalogEventKind.Removed => "removed",
                SunderRpcCatalogEventKind.Activated => "activated",
                SunderRpcCatalogEventKind.Deactivated => "deactivated",
                SunderRpcCatalogEventKind.Faulted => "faulted",
                _ => "reset-required",
            },
            provider = value.Provider is null ? null : ToWire(value.Provider),
        };

    public static object ToWire(SunderRpcContentReference reference)
        => new
        {
            id = reference.Id,
            length = reference.Length,
            sha256 = reference.Sha256,
            mediaType = reference.MediaType,
            fileName = reference.FileName,
            expiresAtUtc = reference.ExpiresAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            repeatability = reference.Repeatability == SunderRpcContentRepeatability.SingleUse
                ? "single-use"
                : "repeatable",
        };

    public static object Error(SunderRpcError error)
    {
        var kind = error.Kind switch
        {
            SunderRpcErrorKind.Domain => "domain",
            SunderRpcErrorKind.PermissionDenied => "permission-denied",
            SunderRpcErrorKind.NotFound => "not-found",
            SunderRpcErrorKind.StaleEndpoint => "stale-endpoint",
            SunderRpcErrorKind.Validation => "validation",
            SunderRpcErrorKind.DeadlineExceeded => "deadline-exceeded",
            SunderRpcErrorKind.Cancelled => "cancelled",
            SunderRpcErrorKind.ResourceExhausted => "resource-exhausted",
            SunderRpcErrorKind.Unavailable => "unavailable",
            SunderRpcErrorKind.ProviderFaulted => "provider-faulted",
            _ => "protocol",
        };
        if (error.Details is { } details)
        {
            return new
            {
                kind,
                error.Code,
                error.Message,
                dispatchState = error.DispatchState == SunderRpcDispatchState.NotDispatched
                    ? "not-dispatched"
                    : "possibly-dispatched",
                retrySafety = error.RetrySafety == SunderRpcRetrySafety.Safe ? "safe" : "unsafe",
                details,
            };
        }
        return new
        {
            kind,
            error.Code,
            error.Message,
            dispatchState = error.DispatchState == SunderRpcDispatchState.NotDispatched
                ? "not-dispatched"
                : "possibly-dispatched",
            retrySafety = error.RetrySafety == SunderRpcRetrySafety.Safe ? "safe" : "unsafe",
        };
    }
}
