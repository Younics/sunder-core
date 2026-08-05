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
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal interface IProcessRuntimeGenerationParticipant
{
    void ActivateGeneration();
}

internal sealed partial class ProcessRuntimeWorker :
    IPackageBackgroundService,
    IPackageRuntimeGenerationParticipant,
    IProcessRuntimeGenerationParticipant,
    IAsyncDisposable
{
    private const int StreamBufferCapacity = 32;
    private readonly ILogger _logger;
    private readonly PreparedRuntimePackage _package;
    private readonly RuntimePackageContext _packageContext;
    private readonly Guid _activationId;
    private readonly RuntimeRpcBroker _broker;
    private readonly SunderWorkerProtocolIdentity _protocol;
    private readonly RuntimeRpcCallerStamp _callerStamp;
    private readonly PackageSessionState? _packageSessionState;
    private readonly RuntimeProcessPolicyOptions _policy;
    private readonly CancellationToken _hostStopping;
    private readonly string _workerTemporaryPath;
    private readonly IReadOnlyList<ProviderIdentity> _expectedProviders;
    private readonly object _stateGate = new();
    private readonly object _callGate = new();
    private readonly Dictionary<string, HostCall> _hostCalls = new(StringComparer.Ordinal);
    private int _pendingHostCalls;
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
    private readonly TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _generationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _forceStopGate = new();
    private readonly string _challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private Process? _process;
    private Channel<byte[]>? _writes;
    private Task _readerTask = Task.CompletedTask;
    private Task _writerTask = Task.CompletedTask;
    private Task _stderrTask = Task.CompletedTask;
    private Task _exitTask = Task.CompletedTask;
    private Task _activationTask = Task.CompletedTask;
    private Task? _forceStopCallbacks;
    private CancellationTokenRegistration _hostStopRegistration;
    private WorkerState _state;
    private long? _sessionGeneration;
    private string? _shutdownId;
    private Exception? _fault;
    private bool _activationAcknowledged;
    private bool _shutdownAcknowledgementReceived;
    private bool _stopStarted;

    public ProcessRuntimeWorker(
        ILogger logger,
        PreparedRuntimePackage package,
        RuntimePackageContext packageContext,
        Guid activationId,
        RuntimeRpcBroker broker,
        RuntimeProcessPolicyOptions? policy = null,
        CancellationToken hostStopping = default,
        SunderWorkerProtocolIdentity? protocol = null,
        PackageSessionState? packageSessionState = null)
    {
        _logger = logger;
        _package = package;
        _packageContext = packageContext;
        _activationId = activationId;
        _broker = broker;
        _protocol = protocol ?? SunderWorkerProtocol.V1;
        _callerStamp = new RuntimeRpcCallerStamp(package.PackageId, activationId);
        _packageSessionState = packageSessionState;
        _policy = ValidatePolicy(policy ?? new RuntimeProcessPolicyOptions());
        _hostStopping = hostStopping;
        _workerTemporaryPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-worker",
            activationId.ToString("N"));
        _expectedProviders = (package.Source.Manifest?.Provides ?? [])
            .Where(static provider => provider is not null
                                      && string.Equals(
                                          provider.Role,
                                          SunderPackageFormat.RuntimeHostRole,
                                          StringComparison.Ordinal))
            .Select(static provider => new ProviderIdentity(
                provider!.ProviderId!,
                provider.ContractId!,
                provider.ContractVersion!,
                provider.ContractSha256!))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    public Task GenerationCompletion => _generationCompletion.Task;

    internal Task ActivationCompletion => _activated.Task;

    internal Sunder.Sdk.Settings.PackageSettingsSchema? SettingsSchema { get; private set; }

    internal string WorkerTemporaryPath => _workerTemporaryPath;

    public Task ComposeAsync(CancellationToken cancellationToken = default)
    {
        if (!_protocol.UsesV2Lifecycle)
        {
            throw new InvalidOperationException("Only sunder.worker.v2 has a separate composition phase.");
        }
        return StartProcessAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _protocol.UsesV2Lifecycle
            ? StartCandidateAsync(cancellationToken)
            : StartProcessAsync(cancellationToken);

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

        try
        {
            var startInfo = CreateStartInfo();
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The process Runtime target did not start.");
            }
            _process = process;
            _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_policy.MaxWriteQueueMessages)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _writerTask = WriteLoopAsync(process.StandardInput.BaseStream);
            _readerTask = ReadLoopAsync(process.StandardOutput.BaseStream);
            _stderrTask = DrainStderrAsync(process.StandardError.BaseStream);
            _exitTask = MonitorExitAsync(process);
            _hostStopRegistration = _hostStopping.Register(static state =>
            {
                var worker = (ProcessRuntimeWorker)state!;
                _ = worker.StopFromHostAsync();
            }, this);

            QueueEnvelope(new
            {
                type = "host.hello",
                protocol = _protocol.Name,
                protocolVersion = _protocol.Version,
                challenge = _challenge,
                packageId = _package.PackageId,
                packageVersion = _package.Version,
                activationId = _activationId.ToString("N"),
                sessionId = _package.SessionId,
                providers = _expectedProviders.Select(static provider => provider.ToWire()).ToArray(),
            });

            using var startupTimeout = new CancellationTokenSource(_policy.StartupTimeout);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _hostStopping,
                startupTimeout.Token);
            try
            {
                await _ready.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                startupTimeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_hostStopping.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Process Runtime target for package '{_package.PackageId}' did not become ready within {_policy.StartupTimeout.TotalSeconds:0.###} seconds.",
                    exception);
            }

            lock (_stateGate)
            {
                ThrowIfFaulted();
                if (_state != WorkerState.Ready)
                {
                    throw new SunderWorkerProtocolException("Process Runtime worker entered an invalid startup state.");
                }
            }
        }
        catch
        {
            await TerminateAfterFailureAsync().ConfigureAwait(false);
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
                throw new InvalidOperationException("The V2 process Runtime worker candidate can only be started once after composition.");
            }
            _state = WorkerState.CandidateStarting;
        }

        QueueEnvelope(new { type = "host.start-candidate" });
        await _candidateStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.CandidateStarted)
            {
                throw new InvalidOperationException("The V2 process worker did not enter the candidate-started state.");
            }
        }
    }

    public Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
        => _protocol.UsesV2Lifecycle
            ? CommitV2GenerationAsync(generation, cancellationToken)
            : CommitV1GenerationAsync(generation, cancellationToken);

    private Task CommitV1GenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation.ActivationId != _activationId)
        {
            throw new InvalidOperationException("The process worker activation does not match the committed Runtime generation.");
        }
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state is not (WorkerState.Ready or WorkerState.Committed))
            {
                throw new InvalidOperationException("The process worker is not ready to commit a Runtime generation.");
            }
            if (_sessionGeneration is { } existing && existing != generation.SessionGeneration)
            {
                throw new InvalidOperationException("The process worker is already committed to another Runtime generation.");
            }
            _sessionGeneration = generation.SessionGeneration;
            _state = WorkerState.Committed;
        }
        return Task.CompletedTask;
    }

    private async Task CommitV2GenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                throw new InvalidOperationException("The V2 process worker is not ready to commit this Runtime generation.");
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
                throw new InvalidOperationException("The V2 process worker did not commit the requested Runtime generation.");
            }
        }
    }

    public void ActivateGeneration()
    {
        long generation;
        Exception? queueFailure = null;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Committed || _sessionGeneration is null)
            {
                throw new InvalidOperationException("The process worker generation has not been committed.");
            }
            generation = _sessionGeneration.Value;
            var body = SunderWorkerProtocol.SerializeFrameBody(new
            {
                type = "host.activate",
                sessionGeneration = generation,
            }, _policy);
            if (_writes is null)
            {
                queueFailure = new InvalidOperationException("Process worker protocol writer is unavailable.");
            }
            else if (!_writes.Writer.TryWrite(body))
            {
                queueFailure = new SunderWorkerProtocolException(
                    "Process worker protocol write queue exceeded its bound.");
            }
            else
            {
                // Calls can observe Active only after host.activate is ordered ahead of them.
                _state = _protocol.UsesV2Lifecycle
                    ? WorkerState.Activating
                    : WorkerState.Active;
            }
        }
        if (queueFailure is not null)
        {
            Fault(queueFailure);
            throw queueFailure;
        }
        _activationTask = EnforceActivationAcknowledgementAsync(generation);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        Task? existingStop = null;
        lock (_stateGate)
        {
            if (_state is WorkerState.Stopped or WorkerState.Created)
            {
                _state = WorkerState.Stopped;
                _stopStarted = true;
                _generationCompletion.TrySetResult();
                _stopped.TrySetResult();
                return;
            }
            if (_stopStarted)
            {
                existingStop = _stopped.Task;
                process = null;
            }
            else
            {
                _stopStarted = true;
                if (_state != WorkerState.Faulted)
                {
                    _state = WorkerState.Stopping;
                }
                process = _process;
                _shutdownId ??= Guid.NewGuid().ToString("N");
            }
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
            await scopeCleanup.ConfigureAwait(false);
            CompleteExpectedStop();
            return;
        }

        try
        {
            if (!process.HasExited && _fault is null)
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
                if (!process.HasExited && _fault is null)
                {
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
                _logger.LogWarning(
                    "Process Runtime target for package {PackageId} did not shut down within its deadline; killing its process tree",
                    _package.PackageId);
                KillProcessTree(process);
            }
        }
        finally
        {
            _writes?.Writer.TryComplete();
            _ = SignalForceStop();
            if (!process.HasExited) KillProcessTree(process);
            await ObserveAsync(_writerTask).ConfigureAwait(false);
            await ObserveAsync(_readerTask).ConfigureAwait(false);
            await ObserveAsync(_stderrTask).ConfigureAwait(false);
            await ObserveAsync(_exitTask).ConfigureAwait(false);
            await ObserveAsync(_activationTask).ConfigureAwait(false);
            if (scopeCleanup.IsCompleted)
            {
                await scopeCleanup.ConfigureAwait(false);
            }
            CleanupAllWorkerPayloads();
            TryDeleteDirectory(_workerTemporaryPath);
            CompleteExpectedStop();
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
        RuntimeCancellation.DisposeAfterCallbacks(_forceStop, SignalForceStop());
    }

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        ReservePendingHostCall();
        var reservationHeld = true;
        try
        {
            await WaitForInvocationReadinessAsync(cancellationToken).ConfigureAwait(false);
            var call = AddHostCall(
                HostCallKind.Unary,
                context,
                providerId,
                serviceId,
                methodId,
                consumePendingReservation: true);
            reservationHeld = false;
            return await InvokeUnaryCoreAsync(
                call,
                providerId,
                context,
                serviceId,
                methodId,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (reservationHeld) ReleasePendingHostCall();
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
        ReservePendingHostCall();
        var reservationHeld = true;
        try
        {
            await WaitForInvocationReadinessAsync(cancellationToken).ConfigureAwait(false);
            var call = AddHostCall(
                HostCallKind.ServerStream,
                context,
                providerId,
                serviceId,
                methodId,
                consumePendingReservation: true);
            reservationHeld = false;
            await foreach (var item in InvokeServerStreamCoreAsync(
                call,
                providerId,
                context,
                serviceId,
                methodId,
                request,
                cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            if (reservationHeld) ReleasePendingHostCall();
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
            if (_protocol.UsesV2Lifecycle && _state == WorkerState.Activating)
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
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        QueueInvocation(call, providerId, context, serviceId, methodId, request);
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        return await call.UnaryCompletion.Task.ConfigureAwait(false);
    }

    private async IAsyncEnumerable<JsonElement> InvokeServerStreamCoreAsync(
        HostCall call,
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        QueueInvocation(call, providerId, context, serviceId, methodId, request);
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        try
        {
            await foreach (var item in call.Events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            if (!call.Terminal) CancelHostCall(call, cancellationToken);
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        if (_package.EntryAssemblyPath is null || _package.SelectedTarget is null)
        {
            throw new InvalidOperationException("A process Runtime target requires a validated declared entry point.");
        }
        var entryPoint = Path.GetFullPath(_package.EntryAssemblyPath);
        var devLaunch = _package.DevProcessLaunch;
        var startInfo = new ProcessStartInfo
        {
            FileName = devLaunch?.NodePath ?? entryPoint,
            WorkingDirectory = Path.GetFullPath(_package.ShadowFolder),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (devLaunch is not null)
        {
            startInfo.ArgumentList.Add(entryPoint);
        }

        startInfo.Environment.Clear();
        var temporaryPath = _workerTemporaryPath;
        Directory.CreateDirectory(temporaryPath);
        RestrictWorkerDirectory(temporaryPath);
        startInfo.Environment["SUNDER_WORKER_PROTOCOL"] = _protocol.Name;
        startInfo.Environment["SUNDER_PACKAGE_ID"] = _package.PackageId;
        startInfo.Environment["SUNDER_PACKAGE_VERSION"] = _package.Version;
        startInfo.Environment["SUNDER_ACTIVATION_ID"] = _activationId.ToString("N");
        startInfo.Environment["SUNDER_SESSION_ID"] = _package.SessionId;
        startInfo.Environment["SUNDER_PACKAGE_CONTENT_PATH"] = Path.GetFullPath(_package.ShadowFolder);
        startInfo.Environment["SUNDER_PACKAGE_DATA_PATH"] = _protocol.UsesV2Lifecycle
            ? Path.GetFullPath(temporaryPath)
            : Path.GetFullPath(_packageContext.LocalStorage.DataRootPath);
        if (!_protocol.UsesV2Lifecycle)
        {
            startInfo.Environment["SUNDER_PACKAGE_STATE_PATH"] = Path.Combine(
                Path.GetFullPath(_packageContext.LocalStorage.DataRootPath),
                "state.json");
        }
        startInfo.Environment["SUNDER_PACKAGE_WORKING_PATH"] = Path.GetFullPath(_package.ShadowFolder);
        startInfo.Environment["TMPDIR"] = temporaryPath;
        startInfo.Environment["TMP"] = temporaryPath;
        startInfo.Environment["TEMP"] = temporaryPath;
        if (OperatingSystem.IsWindows())
        {
            startInfo.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }
        else
        {
            startInfo.Environment["LANG"] = "C.UTF-8";
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
        }
        startInfo.Environment.Remove("NODE_OPTIONS");
        return startInfo;
    }

    private static void RestrictWorkerDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void QueueInvocation(
        HostCall call,
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        QueueEnvelope(new
        {
            type = "host.invoke",
            id = call.Id,
            kind = call.Kind == HostCallKind.Unary ? "unary" : "server-stream",
            providerId,
            serviceId,
            methodId,
            request = request.Clone(),
            context = new
            {
                callerPackageId = context.CallerPackageId,
                callerPackageVersion = context.CallerPackageVersion,
                deadlineUtc = context.DeadlineUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                callDepth = context.CallDepth,
                provider = WorkerWire.ToWire(context.Provider),
            },
        });
    }

    private HostCall AddHostCall(
        HostCallKind kind,
        SunderRpcInvocationContext context,
        string providerId,
        string serviceId,
        string methodId,
        bool consumePendingReservation = false)
    {
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active)
            {
                throw WorkerActivating();
            }
        }
        var call = new HostCall(
            Guid.NewGuid().ToString("N"),
            kind,
            context,
            providerId,
            serviceId,
            methodId);
        lock (_callGate)
        {
            if (!consumePendingReservation
                && _hostCalls.Count + _pendingHostCalls >= _policy.MaxOutstandingHostCalls)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.host-call-limit",
                    "The process worker outstanding call limit was reached."));
            }
            _hostCalls.Add(call.Id, call);
            if (consumePendingReservation)
            {
                if (_pendingHostCalls <= 0)
                {
                    _hostCalls.Remove(call.Id);
                    throw new InvalidOperationException("A process worker Host call reservation was not held.");
                }
                _pendingHostCalls--;
            }
        }
        return call;
    }

    private void ReservePendingHostCall()
    {
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active
                && (!_protocol.UsesV2Lifecycle || _state != WorkerState.Activating))
            {
                throw WorkerActivating();
            }
            lock (_callGate)
            {
                if (_hostCalls.Count + _pendingHostCalls >= _policy.MaxOutstandingHostCalls)
                {
                    throw new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.ResourceExhausted,
                        "rpc.worker.host-call-limit",
                        "The process worker outstanding call limit was reached."));
                }
                _pendingHostCalls++;
            }
        }
    }

    private void ReleasePendingHostCall()
    {
        lock (_callGate)
        {
            if (_pendingHostCalls > 0) _pendingHostCalls--;
        }
    }

    private void CancelHostCall(HostCall call, CancellationToken cancellationToken)
    {
        lock (_callGate)
        {
            if (call.Terminal || call.Cancelled) return;
            call.Cancelled = true;
            call.UnaryCompletion.TrySetCanceled(cancellationToken);
            call.Events.Writer.TryComplete(new OperationCanceledException(cancellationToken));
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
                    _policy,
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
            case "worker.content-discard":
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
                HandleActivated(root);
                break;
            case "worker.candidate-started":
                HandleCandidateStarted(root);
                break;
            case "worker.generation-committed":
                HandleGenerationCommitted(root);
                break;
            case "worker.shutdown-ack":
                HandleShutdownAcknowledgement(root);
                break;
            default:
                throw new SunderWorkerProtocolException($"Unknown process worker envelope type '{type}'.");
        }
    }

    private void HandleReady(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            _protocol.UsesV2Lifecycle
                ?
                [
                    "type",
                    "protocol",
                    "protocolVersion",
                    "challenge",
                    "packageId",
                    "packageVersion",
                    "activationId",
                    "sessionId",
                    "contributions",
                ]
                :
                [
                    "type",
                    "protocol",
                    "protocolVersion",
                    "challenge",
                    "packageId",
                    "packageVersion",
                    "activationId",
                    "sessionId",
                    "providers",
                ]);
        if (!string.Equals(
                SunderWorkerProtocol.RequiredString(root, "protocol", 64),
                _protocol.Name,
                StringComparison.Ordinal)
            || SunderWorkerProtocol.RequiredNonNegativeInt64(root, "protocolVersion") != _protocol.Version
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "challenge", 128), _challenge, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "packageId", 256), _package.PackageId, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "packageVersion", 128), _package.Version, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "activationId", 64), _activationId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "sessionId", 256), _package.SessionId, StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker ready envelope did not bind the exact protocol, challenge, package, activation, and session.");
        }
        JsonElement providersElement;
        if (_protocol.UsesV2Lifecycle)
        {
            var contributions = ReadV2Contributions(
                SunderWorkerProtocol.RequiredValue(root, "contributions"));
            SettingsSchema = contributions.SettingsSchema;
            providersElement = contributions.RpcProviders;
        }
        else
        {
            providersElement = SunderWorkerProtocol.RequiredValue(root, "providers");
        }
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
        _packageContext.PackageSettings.Schema = SettingsSchema;
        lock (_stateGate)
        {
            if (_state != WorkerState.Starting)
            {
                throw new SunderWorkerProtocolException(
                    "Process Runtime worker ready envelope arrived in an invalid state.");
            }
            _state = WorkerState.Ready;
            _ready.TrySetResult();
        }
    }

    private static void RequireEmptyV2ContributionArray(JsonElement contributions, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(contributions, propertyName);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)
        {
            throw new SunderWorkerProtocolException(
                $"This Host slice does not yet support V2 worker '{propertyName}' contributions.");
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
            "contractSha256");
        return new ProviderIdentity(
            SunderWorkerProtocol.RequiredString(value, "providerId", 256),
            SunderWorkerProtocol.RequiredString(value, "contractId", 256),
            SunderWorkerProtocol.RequiredString(value, "contractVersion", 128),
            SunderWorkerProtocol.RequiredString(value, "contractSha256", 64));
    }

    private void HandleCandidateStarted(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type");
        lock (_stateGate)
        {
            if (!_protocol.UsesV2Lifecycle
                || _state is not (WorkerState.CandidateStarting or WorkerState.Stopping)
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

    private void HandleGenerationCommitted(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "activationId", "sessionGeneration");
        var activationId = SunderWorkerProtocol.RequiredString(root, "activationId", 64);
        var generation = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration");
        lock (_stateGate)
        {
            if (!_protocol.UsesV2Lifecycle
                || _state is not (WorkerState.Committing or WorkerState.Stopping)
                || !string.Equals(activationId, _activationId.ToString("N"), StringComparison.Ordinal)
                || generation != _sessionGeneration
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

    private void HandleActivated(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "sessionGeneration");
        var generation = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration");
        lock (_stateGate)
        {
            var validState = _protocol.UsesV2Lifecycle
                ? _state is WorkerState.Activating or WorkerState.Stopping
                : _state is WorkerState.Active or WorkerState.Stopping;
            if (!validState
                || generation != _sessionGeneration
                || _activationAcknowledged)
            {
                throw new SunderWorkerProtocolException("Process worker acknowledged an invalid Runtime generation.");
            }
            _activationAcknowledged = true;
            if (_state == WorkerState.Activating)
            {
                _state = WorkerState.Active;
            }
        }
        _activated.TrySetResult();
    }

    private async Task EnforceActivationAcknowledgementAsync(long generation)
    {
        using var timeout = new CancellationTokenSource(_policy.ActivationTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _forceStop.Token);
        try
        {
            await _activated.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_forceStop.IsCancellationRequested)
        {
            Fault(new TimeoutException(
                $"Process Runtime target for package '{_package.PackageId}' did not acknowledge generation {generation} within {_policy.ActivationTimeout.TotalSeconds:0.###} seconds."));
        }
        catch (OperationCanceledException) when (_forceStop.IsCancellationRequested)
        {
        }
    }

    private void HandleShutdownAcknowledgement(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "shutdownId");
        var shutdownId = SunderWorkerProtocol.RequiredString(root, "shutdownId", 128);
        Task[] workerCallCompletions;
        lock (_stateGate)
        {
            if (_state != WorkerState.Stopping
                || _shutdownId is null
                || !string.Equals(_shutdownId, shutdownId, StringComparison.Ordinal)
                || _shutdownAcknowledgementReceived)
            {
                throw new SunderWorkerProtocolException("Process worker sent an unsolicited or duplicate shutdown acknowledgement.");
            }
            _shutdownAcknowledgementReceived = true;
            lock (_callGate)
            {
                workerCallCompletions = _workerCallCleanup.ToArray();
            }
        }
        _candidateStarted.TrySetResult();
        _generationCommitted.TrySetResult();
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
                    if (!call.Cancelled
                        && !call.Events.Writer.TryWrite(SunderWorkerProtocol.RequiredValue(root, "value").Clone()))
                    {
                        throw new SunderWorkerProtocolException("Process worker stream event queue exceeded its bound.");
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
                    if (!call.Cancelled) call.Events.Writer.TryComplete();
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
                        call.Events.Writer.TryComplete(exception);
                    }
                    return;
            }
        }
    }

    private static Exception ReadWorkerError(JsonElement value, HostCall call)
    {
        SunderWorkerProtocol.RequireOnlyProperties(value, "kind", "code", "message");
        var kindText = SunderWorkerProtocol.RequiredString(value, "kind", 64);
        var code = SunderWorkerProtocol.RequiredString(value, "code", 128);
        if (!code.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new SunderWorkerProtocolException("Process worker returned an unsafe provider error code.");
        }
        var message = SunderWorkerProtocol.RequiredString(value, "message", 512);
        if (kindText == "provider-fault")
        {
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
        if (kindText != "domain")
        {
            throw new SunderWorkerProtocolException(
                $"Process worker attempted to return unauthenticated infrastructure error kind '{kindText}'.");
        }
        return new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Domain, code, message));
    }

    private void BeginWorkerCall(string type, JsonElement root)
    {
        var id = SunderWorkerProtocol.RequiredId(root);
        var control = IsWorkerCleanupControl(type);
        var logging = IsWorkerLogging(type);
        WorkerCall call;
        lock (_stateGate)
        {
            var admitsOrdinary = _state == WorkerState.Active
                                 || _protocol.UsesV2Lifecycle && _state == WorkerState.Activating;
            var admitsLogging = logging
                                && _protocol.UsesV2Lifecycle
                                && !_shutdownAcknowledgementReceived
                                && _state is WorkerState.Ready
                                    or WorkerState.CandidateStarting
                                    or WorkerState.CandidateStarted
                                    or WorkerState.Committing
                                    or WorkerState.Committed
                                    or WorkerState.Activating
                                    or WorkerState.Active
                                    or WorkerState.Stopping;
            if (!(logging
                    ? admitsLogging
                    : admitsOrdinary
                      || _state == WorkerState.Stopping && control && !_shutdownAcknowledgementReceived))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent host call '{type}' before its Runtime generation was active.");
            }
            lock (_callGate)
            {
                if (_rememberedWorkerIds.Contains(id))
                {
                    throw new SunderWorkerProtocolException($"Process worker reused duplicate message id '{id}'.");
                }
                var laneCount = _workerCalls.Values.Count(item =>
                    item.Control == control
                    && item.Logging == logging);
                var laneLimit = control
                    ? _policy.MaxOutstandingWorkerControlCalls
                    : logging
                        ? _policy.MaxOutstandingWorkerLoggingCalls
                    : _policy.MaxOutstandingWorkerCalls;
                if (laneCount >= laneLimit)
                {
                    throw new SunderWorkerProtocolException(
                        control
                            ? "Process worker exceeded its outstanding Host-control-call limit."
                            : logging
                                ? "Process worker exceeded its outstanding logging-call limit."
                            : "Process worker exceeded its outstanding host-call limit.");
                }
                RememberWorkerId(id);
                call = new WorkerCall(
                    id,
                    control,
                    logging,
                    CancellationTokenSource.CreateLinkedTokenSource(_forceStop.Token));
                _workerCalls.Add(id, call);
                _workerCallCleanup.Add(call.Completion.Task);
            }
        }
        var request = root.Clone();
        _ = RunWorkerCallObservedAsync(type, request, call);
    }

    private async Task RunWorkerCallObservedAsync(string type, JsonElement root, WorkerCall call)
    {
        try
        {
            await RunWorkerCallAsync(type, root, call).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.call.cancelled",
                    "The RPC call was cancelled."),
            });
        }
        catch (SunderRpcException exception)
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(exception.Error.Kind, exception.Error.Code, exception.Error.Message),
            });
        }
        catch (ArgumentException) when (IsPackageDataCall(type))
        {
            TryQueueEnvelope(new
            {
                type = "host.error",
                id = call.Id,
                error = WorkerWire.Error(
                    SunderRpcErrorKind.Validation,
                    PackageDataValidationCode(type),
                    "The package data request violates its declared storage contract."),
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
                    SunderRpcErrorKind.Unavailable,
                    "rpc.host.adapter-fault",
                    "The Runtime RPC adapter could not complete the request."),
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
                var scope = ReadOptionalWorkerCallScope(root, "type", "id", "endpointReference");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var provider = scope is null
                    ? await _broker.GetProviderAsync(
                        _callerStamp,
                        endpoint,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.GetProviderAsync(
                        endpoint,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = provider is null ? null : WorkerWire.ToWire(provider) });
                return;
            }
            case "worker.report-invariant-violation":
            {
                RequireV2WorkerCall(type);
                var scope = ReadOptionalWorkerCallScope(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "exceptionMessage");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var exception = new InvalidOperationException(
                    SunderWorkerProtocol.RequiredString(root, "exceptionMessage", 512));
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
                var scope = ReadOptionalWorkerCallScope(root, "type", "id", "contractId");
                var contractId = SunderWorkerProtocol.RequiredString(root, "contractId", 256);
                var snapshot = scope is null
                    ? await _broker.DiscoverAsync(
                        _callerStamp,
                        contractId,
                        call.Cancellation.Token).ConfigureAwait(false)
                    : await scope.Scope.DiscoverAsync(
                        contractId,
                        call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = WorkerWire.ToWire(snapshot) });
                return;
            }
            case "worker.watch":
            {
                var scope = ReadOptionalWorkerCallScope(
                    root,
                    "type",
                    "id",
                    "afterRevision",
                    "afterSequence");
                var afterRevision = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterRevision");
                var afterSequence = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterSequence");
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
                    QueueEnvelope(new { type = "host.event", id = call.Id, value = WorkerWire.ToWire(item) });
                }
                QueueEnvelope(new { type = "host.complete", id = call.Id });
                return;
            }
            case "worker.invoke":
            {
                var scope = ReadOptionalWorkerCallScope(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var serviceId = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
                var methodId = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
                var request = SunderWorkerProtocol.RequiredValue(root, "request").Clone();
                var options = ReadCallOptions(root);
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
                var scope = ReadOptionalWorkerCallScope(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var serviceId = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
                var methodId = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
                var request = SunderWorkerProtocol.RequiredValue(root, "request").Clone();
                var options = ReadCallOptions(root);
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
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "deadlineUtc");
                ISunderRpcCallScope? rpcScope = null;
                WorkerCallScope? workerScope = null;
                try
                {
                    rpcScope = await _broker.CreateCallScopeAsync(
                        _callerStamp,
                        ReadCallOptions(root),
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
                RequireV2WorkerCall(type);
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
                    RequireV2WorkerCall(type);
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
                await using var source = OpenWorkerOwnedContentFile(filePath);
                if (scoped)
                {
                    var scope = GetWorkerCallScope(ReadSafeId(root, "scopeId"));
                    reference = await scope.Scope.RegisterContentAsync(
                        new SunderRpcEndpointReference(
                            SunderWorkerProtocol.RequiredString(root, "endpointReference", 256)),
                        source,
                        options,
                        call.Cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    var hostCall = GetContentHostCall(root);
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
                    RequireV2WorkerCall(type);
                    SunderWorkerProtocol.RequireOnlyProperties(
                        root,
                        "type",
                        "id",
                        "scopeId",
                        "reference");
                    var scope = GetWorkerCallScope(ReadSafeId(root, "scopeId"));
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
                    var hostCall = GetContentHostCall(root);
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
            case "worker.content-discard":
            {
                if (_protocol.UsesV2Lifecycle)
                {
                    throw new SunderWorkerProtocolException(
                        "worker.content-discard is not valid in sunder.worker.v2.");
                }
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "invocationId",
                    "handleId");
                var handleId = ReadSafeId(root, "handleId");
                string filePath;
                try
                {
                    var hostCall = GetContentHostCall(root);
                    lock (_callGate)
                    {
                        if (!hostCall.ContentFiles.Remove(handleId, out filePath!))
                        {
                            throw new SunderWorkerProtocolException(
                                $"Process worker discarded unknown content handle '{handleId}'.");
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
                TryDeleteFile(filePath);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return;
            }
            case "worker.content-release":
            {
                RequireV2WorkerCall(type);
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

    private WorkerCallScope? ReadOptionalWorkerCallScope(
        JsonElement root,
        params string[] unscopedProperties)
    {
        if (!root.TryGetProperty("scopeId", out _))
        {
            SunderWorkerProtocol.RequireOnlyProperties(root, unscopedProperties);
            return null;
        }

        RequireV2WorkerCall(SunderWorkerProtocol.ReadEnvelopeType(root));
        var scopedProperties = new string[unscopedProperties.Length + 1];
        unscopedProperties.CopyTo(scopedProperties, 0);
        scopedProperties[^1] = "scopeId";
        SunderWorkerProtocol.RequireOnlyProperties(root, scopedProperties);
        return GetWorkerCallScope(ReadSafeId(root, "scopeId"));
    }

    private void RequireV2WorkerCall(string type)
    {
        if (!_protocol.UsesV2Lifecycle)
        {
            throw new SunderWorkerProtocolException(
                $"{type} is not valid in sunder.worker.v1.");
        }
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
    {
        var invocationId = ReadSafeId(root, "invocationId");
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
                : !_protocol.UsesV2Lifecycle
                  && IsWithinDirectory(fullPath, _packageContext.LocalStorage.DataRootPath)
                    ? _packageContext.LocalStorage.DataRootPath
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
            if (stream.CanSeek) return stream;
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

    private static SunderRpcContentRegistrationOptions ReadContentRegistrationOptions(JsonElement value)
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
            JsonValueKind.Number when lengthValue.TryGetInt64(out var parsed) && parsed >= 0 => parsed,
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
        return new SunderRpcContentRegistrationOptions(
            SunderWorkerProtocol.RequiredString(value, "mediaType", 128),
            SunderWorkerProtocol.RequiredString(value, "fileName", 255),
            length,
            expiresAtUtc,
            repeatability,
            (int)maximumUses);
    }

    private static SunderRpcContentReference ReadContentReference(JsonElement value)
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
        return new SunderRpcContentReference(
            id,
            SunderWorkerProtocol.RequiredNonNegativeInt64(value, "length"),
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
            or "worker.content-discard"
            or "worker.payload-release";

    private static bool IsWorkerLogging(string type)
        => type is "worker.logging-write" or "worker.provider-fault-diagnostic";

    private static string PackageDataValidationCode(string type)
        => type.StartsWith("worker.settings-", StringComparison.Ordinal)
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
                await SunderWorkerProtocol.WriteFrameBodyAsync(stdin, body, _forceStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_forceStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsStopping()) Fault(new SunderWorkerProtocolException("Process worker protocol writer failed.", exception));
        }
    }

    private async Task DrainStderrAsync(Stream stderr)
    {
        var buffer = new byte[4096];
        var line = new byte[Math.Max(1, _policy.MaxStderrLineCharacters * 4)];
        var lineLength = 0;
        var lineTruncated = false;
        var loggedBytes = 0;
        long receivedBytes = 0;
        var outputTruncated = false;
        try
        {
            while (true)
            {
                var read = await stderr.ReadAsync(buffer, _forceStop.Token).ConfigureAwait(false);
                if (read == 0) break;
                receivedBytes += read;
                if (receivedBytes > _policy.MaxStderrBytes && !outputTruncated)
                {
                    outputTruncated = true;
                    _logger.LogWarning(
                        "Process Runtime stderr for package {PackageId} exceeded {MaximumBytes} bytes; additional stderr is being drained but not logged",
                        _package.PackageId,
                        _policy.MaxStderrBytes);
                }
                if (_protocol.UsesV2Lifecycle)
                {
                    // V2 diagnostics are Host-mediated; raw worker stderr is never persisted.
                    continue;
                }
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        LogStderrLine(line.AsSpan(0, lineLength), lineTruncated, ref loggedBytes, ref outputTruncated);
                        lineLength = 0;
                        lineTruncated = false;
                    }
                    else if (lineLength < line.Length)
                    {
                        line[lineLength++] = value;
                    }
                    else
                    {
                        lineTruncated = true;
                    }
                }
            }
            if (lineLength > 0 || lineTruncated)
            {
                LogStderrLine(line.AsSpan(0, lineLength), lineTruncated, ref loggedBytes, ref outputTruncated);
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

    private void LogStderrLine(
        ReadOnlySpan<byte> bytes,
        bool lineTruncated,
        ref int loggedBytes,
        ref bool outputTruncated)
    {
        if (loggedBytes >= _policy.MaxStderrBytes)
        {
            if (!outputTruncated)
            {
                outputTruncated = true;
                _logger.LogWarning(
                    "Process Runtime stderr for package {PackageId} exceeded {MaximumBytes} bytes; additional stderr is being drained but not logged",
                    _package.PackageId,
                    _policy.MaxStderrBytes);
            }
            return;
        }
        var remaining = _policy.MaxStderrBytes - loggedBytes;
        var selected = bytes[..Math.Min(bytes.Length, remaining)];
        loggedBytes += selected.Length;
        var text = Encoding.UTF8.GetString(selected).TrimEnd('\r');
        text = new string(text.Select(static character => char.IsControl(character) && character != '\t' ? ' ' : character).ToArray());
        if (lineTruncated || selected.Length < bytes.Length) text += " [truncated]";
        if (text.Length > _policy.MaxStderrLineCharacters)
        {
            text = text[.._policy.MaxStderrLineCharacters] + " [truncated]";
        }
        if (text.Length > 0)
        {
            _logger.LogInformation("Process package {PackageId}: {WorkerStderr}", _package.PackageId, text);
        }
    }

    private async Task MonitorExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (!IsStopping())
            {
                Fault(new ProcessRuntimeWorkerException(
                    $"Process Runtime target for package '{_package.PackageId}' exited unexpectedly with code {process.ExitCode}."));
            }
        }
        catch (Exception exception)
        {
            if (!IsStopping()) Fault(exception);
        }
    }

    private void QueueEnvelope(object envelope)
    {
        var body = SunderWorkerProtocol.SerializeFrameBody(envelope, _policy);
        var writes = _writes ?? throw new InvalidOperationException("Process worker protocol writer is unavailable.");
        if (!writes.Writer.TryWrite(body))
        {
            var exception = new SunderWorkerProtocolException("Process worker protocol write queue exceeded its bound.");
            Fault(exception);
            throw exception;
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
            if (!IsStopping()) Fault(exception);
        }
    }

    private void FaultProtocol(SunderWorkerProtocolException exception)
        => Fault(exception, faultDuringStopping: true);

    private void Fault(Exception exception, bool faultDuringStopping = false)
    {
        Process? process;
        lock (_stateGate)
        {
            if (_state is WorkerState.Stopped or WorkerState.Faulted
                || _state == WorkerState.Stopping && !faultDuringStopping)
            {
                return;
            }
            _state = WorkerState.Faulted;
            _fault = exception;
            process = _process;
        }
        _ready.TrySetException(exception);
        if (_protocol.UsesV2Lifecycle)
        {
            _candidateStarted.TrySetResult();
            _generationCommitted.TrySetResult();
        }
        _activated.TrySetException(exception);
        _generationCompletion.TrySetException(exception);
        _shutdownAcknowledged.TrySetException(exception);
        FailOutstandingCalls(exception);
        _ = ObserveAsync(CloseAllWorkerCallScopesAsync());
        _writes?.Writer.TryComplete(exception);
        _ = SignalForceStop();
        if (process is not null) KillProcessTree(process);
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
            call.Events.Writer.TryComplete(exception);
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
        if (_process is { } process && !process.HasExited)
        {
            KillProcessTree(process);
            await ObserveAsync(process.WaitForExitAsync()).ConfigureAwait(false);
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
        lock (_stateGate)
        {
            _state = WorkerState.Stopped;
        }
        _candidateStarted.TrySetResult();
        _generationCommitted.TrySetResult();
        _activated.TrySetResult();
        if (_fault is null) _generationCompletion.TrySetResult();
        _stopped.TrySetResult();
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

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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

    private static RuntimeProcessPolicyOptions ValidatePolicy(RuntimeProcessPolicyOptions policy)
    {
        if (policy.MaxFrameBytes <= 0
            || policy.MaxHeaderBytes <= 0
            || policy.MaxMessageDepth <= 0
            || policy.MaxOutstandingHostCalls <= 0
            || policy.MaxOutstandingWorkerCalls <= 0
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
            || policy.ActivationTimeout <= TimeSpan.Zero
            || policy.ShutdownTimeout <= TimeSpan.Zero
            || policy.CancellationDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Process worker limits and deadlines must be positive.");
        }
        return policy;
    }

    private enum WorkerState
    {
        Created,
        Starting,
        Ready,
        CandidateStarting,
        CandidateStarted,
        Committing,
        Committed,
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
        string methodId)
    {
        public string Id { get; } = id;
        public HostCallKind Kind { get; } = kind;
        public SunderRpcInvocationContext Context { get; } = context;
        public string ProviderId { get; } = providerId;
        public string ServiceId { get; } = serviceId;
        public string MethodId { get; } = methodId;
        public Dictionary<string, string> ContentFiles { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<JsonElement> UnaryCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<JsonElement> Events { get; } = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(StreamBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }
        public bool ProviderFaultDiagnosticPending { get; set; }
        public string? ProviderExceptionType { get; set; }
        public string? ProviderExceptionFingerprint { get; set; }
    }

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
        CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public bool Control { get; } = control;
        public bool Logging { get; } = logging;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task CancellationCallbacks { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; set; }
        public bool HostCancelled { get; set; }
        public bool ClosesWorkerScope { get; set; }
        public int ClosingScopeContentHandles { get; set; }
        public bool ReleasesContentHandle { get; set; }
        public bool ReleasesPayloadHandle { get; set; }
    }

    private sealed record MaterializedWorkerContent(string HandleId, string FilePath);

    private sealed record ProviderIdentity(
        string ProviderId,
        string ContractId,
        string ContractVersion,
        string ContractSha256)
    {
        public object ToWire() => new { providerId = ProviderId, contractId = ContractId, contractVersion = ContractVersion, contractSha256 = ContractSha256 };
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
        => new
        {
            packageId = provider.PackageId,
            packageVersion = provider.PackageVersion,
            providerId = provider.ProviderId,
            contractId = provider.ContractId,
            contractVersion = provider.ContractVersion,
            contractSha256 = provider.ContractSha256,
            activationId = provider.ActivationId.ToString("N"),
            activationEpoch = provider.ActivationEpoch,
            sessionGeneration = provider.SessionGeneration,
            endpointReference = provider.Endpoint.Value,
            catalogRevision = provider.CatalogRevision,
            state = provider.State switch
            {
                SunderRpcProviderState.Active => "active",
                SunderRpcProviderState.Inactive => "inactive",
                _ => "faulted",
            },
            faultCode = provider.FaultCode,
        };

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

    public static object Error(SunderRpcErrorKind kind, string code, string message)
        => new
        {
            kind = kind switch
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
            },
            code,
            message,
        };
}
