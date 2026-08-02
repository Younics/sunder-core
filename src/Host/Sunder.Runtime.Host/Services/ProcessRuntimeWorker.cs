using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal interface IProcessRuntimeGenerationParticipant
{
    void ActivateGeneration();
}

internal sealed class ProcessRuntimeWorker :
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
    private readonly RuntimeRpcCallerStamp _callerStamp;
    private readonly RuntimeProcessPolicyOptions _policy;
    private readonly CancellationToken _hostStopping;
    private readonly IReadOnlyList<ProviderIdentity> _expectedProviders;
    private readonly object _stateGate = new();
    private readonly object _callGate = new();
    private readonly Dictionary<string, HostCall> _hostCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerCall> _workerCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedWorkerIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedWorkerIdOrder = new();
    private readonly CancellationTokenSource _forceStop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    private bool _stopStarted;

    public ProcessRuntimeWorker(
        ILogger logger,
        PreparedRuntimePackage package,
        RuntimePackageContext packageContext,
        Guid activationId,
        RuntimeRpcBroker broker,
        RuntimeProcessPolicyOptions? policy = null,
        CancellationToken hostStopping = default)
    {
        _logger = logger;
        _package = package;
        _packageContext = packageContext;
        _activationId = activationId;
        _broker = broker;
        _callerStamp = new RuntimeRpcCallerStamp(package.PackageId, activationId);
        _policy = ValidatePolicy(policy ?? new RuntimeProcessPolicyOptions());
        _hostStopping = hostStopping;
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
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
                protocol = SunderWorkerProtocol.Name,
                protocolVersion = SunderWorkerProtocol.Version,
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
                if (_state != WorkerState.Starting)
                {
                    throw new SunderWorkerProtocolException("Process Runtime worker entered an invalid startup state.");
                }
                _state = WorkerState.Ready;
            }
        }
        catch
        {
            await TerminateAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
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
                _state = WorkerState.Active;
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
        if (process is null)
        {
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
            CompleteExpectedStop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        CleanupAllWorkerContentFiles();
        _hostStopRegistration.Dispose();
        _process?.Dispose();
        RuntimeCancellation.DisposeAfterCallbacks(_forceStop, SignalForceStop());
    }

    public ValueTask<JsonElement> InvokeUnaryAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
        => InvokeUnaryCoreAsync(
            AddHostCall(HostCallKind.Unary, context),
            providerId,
            context,
            serviceId,
            methodId,
            request,
            cancellationToken);

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
        => InvokeServerStreamCoreAsync(
            AddHostCall(HostCallKind.ServerStream, context),
            providerId,
            context,
            serviceId,
            methodId,
            request,
            cancellationToken);

    private async ValueTask<JsonElement> InvokeUnaryCoreAsync(
        HostCall call,
        string providerId,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        QueueInvocation(call, providerId, context, serviceId, methodId, request);
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
        using var registration = cancellationToken.Register(() => CancelHostCall(call, cancellationToken));
        QueueInvocation(call, providerId, context, serviceId, methodId, request);
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
        var temporaryPath = Path.Combine(_packageContext.LocalStorage.DataRootPath, "tmp");
        Directory.CreateDirectory(temporaryPath);
        startInfo.Environment["SUNDER_WORKER_PROTOCOL"] = SunderWorkerProtocol.Name;
        startInfo.Environment["SUNDER_PACKAGE_ID"] = _package.PackageId;
        startInfo.Environment["SUNDER_PACKAGE_VERSION"] = _package.Version;
        startInfo.Environment["SUNDER_ACTIVATION_ID"] = _activationId.ToString("N");
        startInfo.Environment["SUNDER_SESSION_ID"] = _package.SessionId;
        startInfo.Environment["SUNDER_PACKAGE_CONTENT_PATH"] = Path.GetFullPath(_package.ShadowFolder);
        startInfo.Environment["SUNDER_PACKAGE_DATA_PATH"] = Path.GetFullPath(_packageContext.LocalStorage.DataRootPath);
        startInfo.Environment["SUNDER_PACKAGE_STATE_PATH"] = Path.Combine(
            Path.GetFullPath(_packageContext.LocalStorage.DataRootPath),
            "state.json");
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

    private HostCall AddHostCall(HostCallKind kind, SunderRpcInvocationContext context)
    {
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.worker.activating",
                    "The process Runtime provider is still activating."));
            }
        }
        var call = new HostCall(Guid.NewGuid().ToString("N"), kind, context);
        lock (_callGate)
        {
            if (_hostCalls.Count >= _policy.MaxOutstandingHostCalls)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.host-call-limit",
                    "The process worker outstanding call limit was reached."));
            }
            _hostCalls.Add(call.Id, call);
        }
        return call;
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
        catch (Exception exception)
        {
            Fault(exception is SunderWorkerProtocolException
                ? exception
                : new SunderWorkerProtocolException("Process worker protocol reader failed.", exception));
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
            case "worker.discover":
            case "worker.watch":
            case "worker.invoke":
            case "worker.subscribe":
            case "worker.content-register":
            case "worker.content-open":
            case "worker.content-discard":
                BeginWorkerCall(type, root);
                break;
            case "worker.cancel":
                HandleWorkerCancellation(root);
                break;
            case "worker.activated":
                HandleActivated(root);
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
            "type",
            "protocol",
            "protocolVersion",
            "challenge",
            "packageId",
            "packageVersion",
            "activationId",
            "sessionId",
            "providers");
        if (!string.Equals(
                SunderWorkerProtocol.RequiredString(root, "protocol", 64),
                SunderWorkerProtocol.Name,
                StringComparison.Ordinal)
            || SunderWorkerProtocol.RequiredNonNegativeInt64(root, "protocolVersion") != SunderWorkerProtocol.Version
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "challenge", 128), _challenge, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "packageId", 256), _package.PackageId, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "packageVersion", 128), _package.Version, StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "activationId", 64), _activationId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(SunderWorkerProtocol.RequiredString(root, "sessionId", 256), _package.SessionId, StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker ready envelope did not bind the exact protocol, challenge, package, activation, and session.");
        }
        var providersElement = SunderWorkerProtocol.RequiredValue(root, "providers");
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
        _ready.TrySetResult();
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

    private void HandleActivated(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "sessionGeneration");
        var generation = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration");
        lock (_stateGate)
        {
            if (_state is not (WorkerState.Active or WorkerState.Stopping)
                || generation != _sessionGeneration
                || _activationAcknowledged)
            {
                throw new SunderWorkerProtocolException("Process worker acknowledged an invalid Runtime generation.");
            }
            _activationAcknowledged = true;
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
        lock (_stateGate)
        {
            if (_state != WorkerState.Stopping
                || _shutdownId is null
                || !string.Equals(_shutdownId, shutdownId, StringComparison.Ordinal)
                || _shutdownAcknowledged.Task.IsCompleted)
            {
                throw new SunderWorkerProtocolException("Process worker sent an unsolicited or duplicate shutdown acknowledgement.");
            }
        }
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
                        : ReadWorkerError(SunderWorkerProtocol.RequiredValue(root, "error"));
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

    private static Exception ReadWorkerError(JsonElement value)
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
            return new ProcessRpcProviderFaultException(code);
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
        lock (_stateGate)
        {
            if (_state != WorkerState.Active)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent host call '{type}' before its Runtime generation was active.");
            }
        }
        var id = SunderWorkerProtocol.RequiredId(root);
        WorkerCall call;
        lock (_callGate)
        {
            if (_rememberedWorkerIds.Contains(id))
            {
                throw new SunderWorkerProtocolException($"Process worker reused duplicate message id '{id}'.");
            }
            if (_workerCalls.Count >= _policy.MaxOutstandingWorkerCalls)
            {
                throw new SunderWorkerProtocolException("Process worker exceeded its outstanding host-call limit.");
            }
            RememberWorkerId(id);
            call = new WorkerCall(id, CancellationTokenSource.CreateLinkedTokenSource(_forceStop.Token));
            _workerCalls.Add(id, call);
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
        catch (SunderWorkerProtocolException exception)
        {
            Fault(exception);
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
                cancellationCallbacks = call.CancellationCallbacks;
            }
            await cancellationCallbacks.ConfigureAwait(false);
            call.Cancellation.Dispose();
        }
    }

    private async Task RunWorkerCallAsync(string type, JsonElement root, WorkerCall call)
    {
        switch (type)
        {
            case "worker.get-provider":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "endpointReference");
                var endpoint = new SunderRpcEndpointReference(
                    SunderWorkerProtocol.RequiredString(root, "endpointReference", 256));
                var provider = await _broker.GetProviderAsync(
                    _callerStamp,
                    endpoint,
                    call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = provider is null ? null : WorkerWire.ToWire(provider) });
                return;
            }
            case "worker.discover":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "contractId");
                var snapshot = await _broker.DiscoverAsync(
                    _callerStamp,
                    SunderWorkerProtocol.RequiredString(root, "contractId", 256),
                    call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = WorkerWire.ToWire(snapshot) });
                return;
            }
            case "worker.watch":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "afterRevision", "afterSequence");
                await foreach (var item in _broker.WatchAsync(
                                   _callerStamp,
                                   SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterRevision"),
                                   SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterSequence"),
                                   call.Cancellation.Token).ConfigureAwait(false))
                {
                    QueueEnvelope(new { type = "host.event", id = call.Id, value = WorkerWire.ToWire(item) });
                }
                QueueEnvelope(new { type = "host.complete", id = call.Id });
                return;
            }
            case "worker.invoke":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc");
                var value = await _broker.InvokeAsync(
                    _callerStamp,
                    new SunderRpcEndpointReference(SunderWorkerProtocol.RequiredString(root, "endpointReference", 256)),
                    SunderWorkerProtocol.RequiredString(root, "serviceId", 128),
                    SunderWorkerProtocol.RequiredString(root, "methodId", 128),
                    SunderWorkerProtocol.RequiredValue(root, "request").Clone(),
                    ReadCallOptions(root),
                    call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value });
                return;
            }
            case "worker.subscribe":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "endpointReference",
                    "serviceId",
                    "methodId",
                    "request",
                    "deadlineUtc");
                await foreach (var item in _broker.SubscribeAsync(
                                   _callerStamp,
                                   new SunderRpcEndpointReference(SunderWorkerProtocol.RequiredString(root, "endpointReference", 256)),
                                   SunderWorkerProtocol.RequiredString(root, "serviceId", 128),
                                   SunderWorkerProtocol.RequiredString(root, "methodId", 128),
                                   SunderWorkerProtocol.RequiredValue(root, "request").Clone(),
                                   ReadCallOptions(root),
                                   call.Cancellation.Token).ConfigureAwait(false))
                {
                    QueueEnvelope(new { type = "host.event", id = call.Id, value = item });
                }
                QueueEnvelope(new { type = "host.complete", id = call.Id });
                return;
            }
            case "worker.content-register":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "invocationId",
                    "filePath",
                    "options");
                var hostCall = GetContentHostCall(root);
                var reference = await hostCall.Context.RegisterContentFileAsync(
                    ResolveWorkerOwnedFilePath(SunderWorkerProtocol.RequiredString(root, "filePath", 4096)),
                    ReadContentRegistrationOptions(SunderWorkerProtocol.RequiredValue(root, "options")),
                    call.Cancellation.Token).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = WorkerWire.ToWire(reference) });
                return;
            }
            case "worker.content-open":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "invocationId",
                    "reference");
                var hostCall = GetContentHostCall(root);
                var reference = ReadContentReference(SunderWorkerProtocol.RequiredValue(root, "reference"));
                await using var source = await hostCall.Context.OpenContentAsync(
                    reference,
                    call.Cancellation.Token).ConfigureAwait(false);
                var contentDirectory = Path.Combine(
                    _packageContext.LocalStorage.DataRootPath,
                    "tmp",
                    "rpc-content");
                Directory.CreateDirectory(contentDirectory);
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
                        await source.CopyToAsync(destination, 128 * 1024, call.Cancellation.Token)
                            .ConfigureAwait(false);
                        await destination.FlushAsync(call.Cancellation.Token).ConfigureAwait(false);
                    }
                    if (new FileInfo(filePath).Length != reference.Length)
                    {
                        throw new SunderWorkerProtocolException(
                            "Host-mediated RPC content length changed while materializing it for a process worker.");
                    }
                    lock (_callGate)
                    {
                        if (!_hostCalls.TryGetValue(hostCall.Id, out var activeCall)
                            || !ReferenceEquals(activeCall, hostCall)
                            || hostCall.Terminal
                            || hostCall.Cancelled)
                        {
                            throw new OperationCanceledException(call.Cancellation.Token);
                        }
                        hostCall.ContentFiles.Add(handleId, filePath);
                    }
                    QueueEnvelope(new
                    {
                        type = "host.result",
                        id = call.Id,
                        value = new { handleId, filePath },
                    });
                }
                catch
                {
                    TryDeleteFile(filePath);
                    throw;
                }
                return;
            }
            case "worker.content-discard":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    root,
                    "type",
                    "id",
                    "invocationId",
                    "handleId");
                var hostCall = GetContentHostCall(root);
                var handleId = ReadSafeId(root, "handleId");
                string filePath;
                lock (_callGate)
                {
                    if (!hostCall.ContentFiles.Remove(handleId, out filePath!))
                    {
                        throw new SunderWorkerProtocolException(
                            $"Process worker discarded unknown content handle '{handleId}'.");
                    }
                }
                TryDeleteFile(filePath);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return;
            }
            default:
                throw new SunderWorkerProtocolException($"Unknown worker Host-call type '{type}'.");
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
        if (!IsWithinDirectory(fullPath, _package.ShadowFolder)
            && !IsWithinDirectory(fullPath, _packageContext.LocalStorage.DataRootPath))
        {
            throw new SunderWorkerProtocolException(
                "Process worker content files must be inside the package content or private data directory.");
        }
        return fullPath;
    }

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
            if (!_workerCalls.TryGetValue(id, out var call) || call.Cancelled)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent an unsolicited or duplicate cancellation for '{id}'.");
            }
            call.Cancelled = true;
            call.CancellationCallbacks = RuntimeCancellation.Signal(call.Cancellation);
        }
    }

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

    private void Fault(Exception exception)
    {
        Process? process;
        lock (_stateGate)
        {
            if (_state is WorkerState.Stopped or WorkerState.Stopping or WorkerState.Faulted) return;
            _state = WorkerState.Faulted;
            _fault = exception;
            process = _process;
        }
        _ready.TrySetException(exception);
        _generationCompletion.TrySetException(exception);
        _shutdownAcknowledged.TrySetException(exception);
        FailOutstandingCalls(exception);
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
                if (!call.Cancelled)
                {
                    call.Cancelled = true;
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
        Committed,
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
        SunderRpcInvocationContext context)
    {
        public string Id { get; } = id;
        public HostCallKind Kind { get; } = kind;
        public SunderRpcInvocationContext Context { get; } = context;
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
    }

    private sealed class WorkerCall(string id, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task CancellationCallbacks { get; set; } = Task.CompletedTask;
        public bool Cancelled { get; set; }
    }

    private sealed record ProviderIdentity(
        string ProviderId,
        string ContractId,
        string ContractVersion,
        string ContractSha256)
    {
        public object ToWire() => new { providerId = ProviderId, contractId = ContractId, contractVersion = ContractVersion, contractSha256 = ContractSha256 };
    }
}

internal sealed class ProcessRpcProviderFaultException(string code)
    : Exception($"Process RPC provider reported fault '{code}'.");

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
