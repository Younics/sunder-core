using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class SunderWorkerRuntime
{
    private readonly Func<WorkerRpcClient, WorkerRuntimeConfiguration> _configure;
    private readonly WorkerProtocolIdentity _protocol;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly WorkerDiagnosticSink _diagnostics;
    private readonly WorkerFrameReader _reader;
    private readonly WorkerProtocolOutput _output;
    private readonly WorkerRpcClient _client;
    private IReadOnlyDictionary<string, SunderWorkerProviderRegistration> _providers =
        new Dictionary<string, SunderWorkerProviderRegistration>(StringComparer.Ordinal);
    private WorkerProviderIdentity[] _providerIdentities = [];
    private WorkerRuntimeConfiguration? _configuration;
    private readonly Dictionary<string, InboundInvocation> _invocations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedHostIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedHostIdOrder = new();
    private readonly CancellationTokenSource _protocolStop = new();
    private readonly CancellationTokenSource _workStop = new();
    private Task _activationOperation = Task.CompletedTask;
    private Task _candidateStartOperation = Task.CompletedTask;
    private Task _generationCommitOperation = Task.CompletedTask;
    private Task _shutdownOperation = Task.CompletedTask;
    private RuntimePhase _phase;
    private long? _sessionGeneration;
    private bool _generationCommitAcknowledgementPending;
    private bool _loggingClosed;
    private Exception? _failure;

    public SunderWorkerRuntime(
        Func<WorkerRpcClient, WorkerRuntimeConfiguration> configure,
        WorkerProtocolIdentity protocol,
        Stream input,
        Stream output,
        WorkerDiagnosticSink diagnostics,
        WorkerEnvironment environment,
        WorkerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure = configure;
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _environment = environment;
        _limits = limits;
        _diagnostics = diagnostics;
        _reader = new WorkerFrameReader(input, limits);
        _output = new WorkerProtocolOutput(output, limits, _protocolStop.Token);
        _client = new WorkerRpcClient(this, _output, environment, limits);
    }

    internal object Gate { get; } = new();
    internal CancellationToken StoppingToken => _protocolStop.Token;
    internal bool IsActiveUnderGate => _phase is RuntimePhase.Activating or RuntimePhase.Active;
    internal bool CanSendControlUnderGate => _phase is RuntimePhase.Activating or RuntimePhase.Active or RuntimePhase.Stopping;
    internal bool CanSendLoggingUnderGate => !_loggingClosed && _protocol.UsesV2Lifecycle && _phase is
        RuntimePhase.Ready
        or RuntimePhase.CandidateStarting
        or RuntimePhase.CandidateStarted
        or RuntimePhase.Committing
        or RuntimePhase.Committed
        or RuntimePhase.Activating
        or RuntimePhase.Active
        or RuntimePhase.Stopping;
    internal bool UsesV2Protocol => _protocol.UsesV2Lifecycle;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((SunderWorkerRuntime)state!).CancelFromCaller(),
            this);
        try
        {
            while (true)
            {
                using var document = await _reader.ReadAsync(_protocolStop.Token).ConfigureAwait(false);
                if (await HandleEnvelopeAsync(document.RootElement).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested)
        {
            Exception? failure;
            bool stopped;
            lock (Gate)
            {
                failure = _failure;
                stopped = _phase == RuntimePhase.Stopped;
            }
            if (failure is not null) throw failure;
            if (stopped) return;
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception)
        {
            var failure = exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("Sunder worker protocol processing failed.", exception);
            Fail(failure);
            throw failure;
        }
        finally
        {
            CompleteRuntime();
        }
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InboundInvocation[] invocations;
        lock (Gate)
        {
            if (_phase is RuntimePhase.Stopped or RuntimePhase.Faulted) return;
            _phase = RuntimePhase.Faulted;
            _failure = exception;
            invocations = _invocations.Values.ToArray();
        }

        _diagnostics.Write($"{_protocol.Name} worker failure", exception);
        CancelSource(_workStop);
        foreach (var invocation in invocations) CancelInvocation(invocation);
        _client.FailAll(exception);
        CancelSource(_protocolStop);
    }

    private async ValueTask<bool> HandleEnvelopeAsync(JsonElement root)
    {
        var type = WorkerJson.ReadEnvelopeType(root);
        lock (Gate)
        {
            if (_phase == RuntimePhase.WaitingForHello
                && !string.Equals(type, "host.hello", StringComparison.Ordinal))
            {
                throw new WorkerProtocolException($"Host sent '{type}' before the required host.hello envelope.");
            }
        }

        switch (type)
        {
            case "host.hello":
                await HandleHelloAsync(root).ConfigureAwait(false);
                return false;
            case "host.activate":
                await HandleActivateAsync(root).ConfigureAwait(false);
                return false;
            case "host.start-candidate":
                BeginStartCandidate(root);
                return false;
            case "host.commit-generation":
                BeginCommitGeneration(root);
                return false;
            case "host.invoke":
                BeginInvocation(root);
                return false;
            case "host.cancel":
                HandleInvocationCancellation(root);
                return false;
            case "host.result":
            case "host.event":
            case "host.complete":
            case "host.error":
                _client.HandleResponse(type, root);
                return false;
            case "host.shutdown":
                BeginShutdown(root);
                return false;
            default:
                throw new WorkerProtocolException($"Unknown Host envelope type '{type}'.");
        }
    }

    private async ValueTask HandleHelloAsync(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(
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
        lock (Gate)
        {
            if (_phase != RuntimePhase.WaitingForHello)
            {
                throw new WorkerProtocolException("host.hello is duplicate or out of order.");
            }
        }

        var protocol = WorkerJson.RequiredString(root, "protocol", 64);
        var protocolVersion = WorkerJson.RequiredNonNegativeInt64(root, "protocolVersion");
        var challenge = WorkerJson.RequiredString(root, "challenge", 128);
        var packageId = WorkerJson.RequiredString(root, "packageId", 256);
        var packageVersion = WorkerJson.RequiredString(root, "packageVersion", 128);
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var sessionId = WorkerJson.RequiredSafeId(root, "sessionId", 256);
        if (!string.Equals(protocol, _protocol.Name, StringComparison.Ordinal)
            || protocolVersion != _protocol.Version
            || !string.Equals(protocol, _environment.Protocol, StringComparison.Ordinal)
            || !string.Equals(packageId, _environment.PackageId, StringComparison.Ordinal)
            || !string.Equals(packageVersion, _environment.PackageVersion, StringComparison.Ordinal)
            || !string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal)
            || !string.Equals(sessionId, _environment.SessionId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                "host.hello does not match the exact protocol and sanitized process activation environment.");
        }

        ConfigureForActivation();

        var providersValue = WorkerJson.RequiredValue(root, "providers");
        if (providersValue.ValueKind != JsonValueKind.Array)
        {
            throw new WorkerProtocolException("Host hello providers must be an array.");
        }
        var declaredProviders = providersValue.EnumerateArray()
            .Select(WorkerWire.ReadProviderIdentity)
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
        if (!declaredProviders.SequenceEqual(_providerIdentities))
        {
            throw new WorkerProtocolException(
                "Registered providers do not exactly match the Host-declared Runtime providers.");
        }

        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.ready");
            writer.WriteString("protocol", _protocol.Name);
            writer.WriteNumber("protocolVersion", _protocol.Version);
            writer.WriteString("challenge", challenge);
            writer.WriteString("packageId", packageId);
            writer.WriteString("packageVersion", packageVersion);
            writer.WriteString("activationId", activationId);
            writer.WriteString("sessionId", sessionId);
            if (_protocol.UsesV2Lifecycle)
            {
                WriteV2Contributions(writer);
            }
            else
            {
                writer.WritePropertyName("providers");
                writer.WriteStartArray();
                foreach (var provider in _providerIdentities) WorkerWire.WriteProviderIdentity(writer, provider);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
        lock (Gate)
        {
            if (_phase != RuntimePhase.WaitingForHello)
            {
                throw new WorkerProtocolException("Worker state changed while completing host.hello.");
            }
            _phase = RuntimePhase.Ready;
        }
    }

    private void ConfigureForActivation()
    {
        if (_configuration is not null)
        {
            throw new WorkerProtocolException("Worker activation configuration ran more than once.");
        }
        var configuration = _configure(_client)
                            ?? throw new InvalidOperationException("Worker configuration returned null.");
        _configuration = configuration;
        _providers = configuration.Providers.ToDictionary(
            static provider => provider.ProviderId,
            StringComparer.Ordinal);
        _providerIdentities = configuration.Providers
            .Select(static provider => new WorkerProviderIdentity(
                provider.ProviderId,
                provider.ContractId,
                provider.ContractVersion,
                provider.ContractSha256))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    private WorkerRuntimeConfiguration Configuration
        => _configuration ?? throw new WorkerProtocolException("Worker activation is not configured.");

    private void WriteV2Contributions(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("contributions");
        writer.WriteStartObject();
        writer.WritePropertyName("settingsSchema");
        if (Configuration.SettingsSchema is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WorkerWire.WriteSettingsSchema(writer, Configuration.SettingsSchema);
        }
        writer.WritePropertyName("runtimeOperations");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("runtimeStreams");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("callbackHandlers");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteBoolean("authHandler", false);
        writer.WritePropertyName("rpcProviders");
        writer.WriteStartArray();
        foreach (var provider in _providerIdentities) WorkerWire.WriteProviderIdentity(writer, provider);
        writer.WriteEndArray();
        writer.WriteBoolean("candidateLifecycle", Configuration.OnCandidateStarted is not null);
        writer.WriteBoolean("generationLifecycle", Configuration.OnGenerationCommitted is not null);
        writer.WriteEndObject();
    }

    private void BeginStartCandidate(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type");
        if (!_protocol.UsesV2Lifecycle)
        {
            throw new WorkerProtocolException("host.start-candidate is not valid in sunder.worker.v1.");
        }
        lock (Gate)
        {
            if (_phase != RuntimePhase.Ready)
            {
                throw new WorkerProtocolException("host.start-candidate is duplicate or out of order.");
            }
            _phase = RuntimePhase.CandidateStarting;
        }
        _candidateStartOperation = CompleteCandidateStartAsync();
    }

    private async Task CompleteCandidateStartAsync()
    {
        try
        {
            if (Configuration.OnCandidateStarted is not null)
            {
                await Configuration.OnCandidateStarted(_workStop.Token).ConfigureAwait(false);
            }

            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                if (_phase == RuntimePhase.Stopping) return;
                if (_phase != RuntimePhase.CandidateStarting)
                {
                    throw new WorkerProtocolException("Worker state changed while starting the candidate.");
                }
                _phase = RuntimePhase.CandidateStarted;
            }
            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.candidate-started");
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker candidate-start callback failed.", exception));
        }
    }

    private void BeginCommitGeneration(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "activationId", "sessionGeneration");
        if (!_protocol.UsesV2Lifecycle)
        {
            throw new WorkerProtocolException("host.commit-generation is not valid in sunder.worker.v1.");
        }
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var sessionGeneration = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        if (!string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("host.commit-generation does not identify this worker activation.");
        }

        var invokeCallback = false;
        lock (Gate)
        {
            if (_phase == RuntimePhase.Committed && _sessionGeneration == sessionGeneration)
            {
                // Publication reconciliation may retry the exact commit.
                if (_generationCommitAcknowledgementPending) return;
                _generationCommitAcknowledgementPending = true;
            }
            else if (_phase == RuntimePhase.Committing && _sessionGeneration == sessionGeneration)
            {
                // The original callback will publish the acknowledgement for this exact retry.
                return;
            }
            else if (_phase == RuntimePhase.CandidateStarted && _sessionGeneration is null)
            {
                _sessionGeneration = sessionGeneration;
                _phase = RuntimePhase.Committing;
                invokeCallback = true;
            }
            else
            {
                throw new WorkerProtocolException("host.commit-generation is duplicate, conflicting, or out of order.");
            }
        }
        _generationCommitOperation = CompleteGenerationCommitAsync(
            activationId,
            sessionGeneration,
            invokeCallback);
    }

    private async Task CompleteGenerationCommitAsync(
        string activationId,
        long sessionGeneration,
        bool invokeCallback)
    {
        try
        {
            if (invokeCallback)
            {
                if (Configuration.OnGenerationCommitted is not null)
                {
                    await Configuration.OnGenerationCommitted(
                        new SunderWorkerGenerationContext(_environment.ActivationGuid, sessionGeneration),
                        _workStop.Token).ConfigureAwait(false);
                }

                lock (Gate)
                {
                    ThrowIfFaultedUnderGate();
                    if (_phase == RuntimePhase.Stopping) return;
                    if (_phase != RuntimePhase.Committing || _sessionGeneration != sessionGeneration)
                    {
                        throw new WorkerProtocolException("Worker state changed while committing the Runtime generation.");
                    }
                    _phase = RuntimePhase.Committed;
                    _generationCommitAcknowledgementPending = true;
                }
            }

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.generation-committed");
                writer.WriteString("activationId", activationId);
                writer.WriteNumber("sessionGeneration", sessionGeneration);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
            lock (Gate)
            {
                if (_phase == RuntimePhase.Committed && _sessionGeneration == sessionGeneration)
                {
                    _generationCommitAcknowledgementPending = false;
                }
            }
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker generation-commit callback failed.", exception));
        }
    }

    private async ValueTask HandleActivateAsync(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "sessionGeneration");
        var sessionGeneration = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        if (_protocol.UsesV2Lifecycle)
        {
            lock (Gate)
            {
                if (_phase != RuntimePhase.Committed || _sessionGeneration != sessionGeneration)
                {
                    throw new WorkerProtocolException("host.activate is duplicate or out of order.");
                }
                _phase = RuntimePhase.Activating;
            }
            _activationOperation = CompleteV2ActivationAsync(sessionGeneration);
            return;
        }

        lock (Gate)
        {
            if (_phase != RuntimePhase.Ready)
            {
                throw new WorkerProtocolException("host.activate is duplicate or out of order.");
            }
            _sessionGeneration = sessionGeneration;
            _phase = RuntimePhase.Active;
        }

        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.activated");
            writer.WriteNumber("sessionGeneration", sessionGeneration);
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
        _activationOperation = ObserveActivationAsync();
    }

    private async Task CompleteV2ActivationAsync(long sessionGeneration)
    {
        try
        {
            if (Configuration.OnActivated is not null)
            {
                await Configuration.OnActivated(_client, _workStop.Token).ConfigureAwait(false);
            }

            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                if (_phase == RuntimePhase.Stopping) return;
                if (_phase != RuntimePhase.Activating || _sessionGeneration != sessionGeneration)
                {
                    throw new WorkerProtocolException("Worker state changed while activating the Runtime generation.");
                }
                _phase = RuntimePhase.Active;
            }

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.activated");
                writer.WriteNumber("sessionGeneration", sessionGeneration);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (SunderRpcException exception) when (
            IsStopping()
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable
            && exception.Error.Code == "rpc.worker.not-active")
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker activation callback failed.", exception));
        }
    }

    private async Task ObserveActivationAsync()
    {
        if (Configuration.OnActivated is null) return;
        try
        {
            await Configuration.OnActivated(_client, _workStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (SunderRpcException exception) when (
            IsStopping()
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable
            && exception.Error.Code == "rpc.worker.not-active")
        {
        }
        catch (Exception exception)
        {
            Fail(new WorkerProtocolException("The worker activation callback failed.", exception));
        }
    }

    private void BeginInvocation(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(
            root,
            "type",
            "id",
            "kind",
            "providerId",
            "serviceId",
            "methodId",
            "request",
            "context");
        var id = WorkerJson.RequiredSafeId(root, "id");
        var kind = WorkerJson.RequiredString(root, "kind", 32) switch
        {
            "unary" => InboundInvocationKind.Unary,
            "server-stream" => InboundInvocationKind.ServerStream,
            _ => throw new WorkerProtocolException("Host invocation kind is invalid."),
        };
        var providerId = WorkerJson.RequiredString(root, "providerId", 256);
        if (!_providers.TryGetValue(providerId, out var registration))
        {
            throw new WorkerProtocolException($"Host invoked undeclared provider '{providerId}'.");
        }
        var serviceId = WorkerJson.RequiredString(root, "serviceId", 128);
        var methodId = WorkerJson.RequiredString(root, "methodId", 128);
        var request = WorkerJson.RequiredValue(root, "request").Clone();

        var contextValue = WorkerJson.RequiredValue(root, "context");
        WorkerJson.RequireOnlyProperties(
            contextValue,
            "callerPackageId",
            "callerPackageVersion",
            "deadlineUtc",
            "callDepth",
            "provider");
        var callerPackageId = WorkerJson.RequiredString(contextValue, "callerPackageId", 256);
        var callerPackageVersion = WorkerJson.RequiredString(contextValue, "callerPackageVersion", 128);
        var deadlineUtc = WorkerJson.RequiredUtcTimestamp(contextValue, "deadlineUtc");
        var callDepthValue = WorkerJson.RequiredNonNegativeInt64(contextValue, "callDepth");
        if (callDepthValue is < 1 or > int.MaxValue)
        {
            throw new WorkerProtocolException("Host invocation callDepth is invalid.");
        }
        var provider = WorkerWire.ReadProviderSnapshot(WorkerJson.RequiredValue(contextValue, "provider"));

        long sessionGeneration;
        lock (Gate)
        {
            if (_phase != RuntimePhase.Active || _sessionGeneration is null)
            {
                throw new WorkerProtocolException("Host invocation arrived before activation.");
            }
            sessionGeneration = _sessionGeneration.Value;
        }
        if (!string.Equals(provider.PackageId, _environment.PackageId, StringComparison.Ordinal)
            || !string.Equals(provider.PackageVersion, _environment.PackageVersion, StringComparison.Ordinal)
            || provider.ActivationId != _environment.ActivationGuid
            || provider.SessionGeneration < sessionGeneration
            || provider.State != SunderRpcProviderState.Active
            || provider.FaultCode is not null
            || !string.Equals(provider.ProviderId, registration.ProviderId, StringComparison.Ordinal)
            || !string.Equals(provider.ContractId, registration.ContractId, StringComparison.Ordinal)
            || !string.Equals(provider.ContractVersion, registration.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(provider.ContractSha256, registration.ContractSha256, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                "Host invocation context does not identify the exact registered provider activation.");
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_workStop.Token);
        var authority = new WorkerInvocationAuthority(
            this,
            _client,
            _environment,
            _limits,
            id,
            cancellation.Token);
        var context = new SunderRpcInvocationContext(
            callerPackageId,
            callerPackageVersion,
            provider,
            deadlineUtc,
            (int)callDepthValue,
            cancellation.Token,
            authority);
        var invocation = new InboundInvocation(
            id,
            kind,
            cancellation,
            authority,
            context,
            serviceId,
            methodId);
        lock (Gate)
        {
            if (_phase != RuntimePhase.Active)
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException("Host invocation arrived while the worker was stopping.");
            }
            if (_rememberedHostIds.Contains(id))
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException($"Host reused duplicate invocation id '{id}'.");
            }
            RememberHostId(id);
            if (_invocations.Count >= _limits.MaximumInboundCalls)
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException("Host exceeded the worker outstanding invocation limit.");
            }
            _invocations.Add(id, invocation);
        }

        invocation.Operation = RunInvocationAsync(
            invocation,
            registration.Handler,
            serviceId,
            methodId,
            request);
    }

    private async Task RunInvocationAsync(
        InboundInvocation invocation,
        ISunderRpcServiceHandler handler,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        // Provider code may issue a nested Host RPC before its first asynchronous yield. Keep that
        // synchronous prefix off the protocol reader so it can consume the nested response.
        await Task.Yield();
        try
        {
            if (invocation.Kind == InboundInvocationKind.Unary)
            {
                await RunUnaryInvocationAsync(
                    invocation,
                    handler,
                    serviceId,
                    methodId,
                    request).ConfigureAwait(false);
            }
            else
            {
                await RunStreamInvocationAsync(
                    invocation,
                    handler,
                    serviceId,
                    methodId,
                    request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("Worker provider response transport failed.", exception));
        }
        finally
        {
            lock (Gate)
            {
                _invocations.Remove(invocation.Id);
            }
            invocation.Authority.Revoke();
            invocation.Cancellation.Dispose();
        }
    }

    private async Task RunUnaryInvocationAsync(
        InboundInvocation invocation,
        ISunderRpcServiceHandler handler,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        JsonElement result;
        try
        {
            result = await handler.InvokeUnaryAsync(
                invocation.Context,
                serviceId,
                methodId,
                request,
                invocation.Cancellation.Token).ConfigureAwait(false);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
            return;
        }

        if (ShouldSuppressResponse(invocation)) return;
        byte[] body;
        try
        {
            body = SerializeResult("worker.result", invocation.Id, result);
        }
        catch (Exception exception)
        {
            await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
            return;
        }
        await _output.WriteAsync(body).ConfigureAwait(false);
    }

    private async Task RunStreamInvocationAsync(
        InboundInvocation invocation,
        ISunderRpcServiceHandler handler,
        string serviceId,
        string methodId,
        JsonElement request)
    {
        IAsyncEnumerable<JsonElement> stream;
        try
        {
            stream = handler.InvokeServerStreamAsync(
                         invocation.Context,
                         serviceId,
                         methodId,
                         request,
                         invocation.Cancellation.Token)
                     ?? throw new InvalidOperationException("Provider returned a null server stream.");
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
            return;
        }

        try
        {
            await foreach (var item in stream
                               .WithCancellation(invocation.Cancellation.Token)
                               .ConfigureAwait(false))
            {
                if (ShouldSuppressResponse(invocation)) return;
                byte[] body;
                try
                {
                    body = SerializeResult("worker.event", invocation.Id, item);
                }
                catch (Exception exception)
                {
                    await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
                    return;
                }
                await _output.WriteAsync(body).ConfigureAwait(false);
            }
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
            return;
        }

        if (ShouldSuppressResponse(invocation)) return;
        var complete = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.complete");
            writer.WriteString("id", invocation.Id);
            writer.WriteEndObject();
        });
        await _output.WriteAsync(complete).ConfigureAwait(false);
    }

    private async ValueTask WriteProviderFailureAsync(InboundInvocation invocation, Exception exception)
    {
        if (ShouldSuppressResponse(invocation)) return;
        string kind;
        string code;
        string message;
        if (invocation.HostCancelled)
        {
            kind = "cancelled";
            code = "rpc.call.cancelled";
            message = "The RPC call was cancelled.";
        }
        else if (exception is SunderRpcException rpcException
                 && rpcException.Error.Kind == SunderRpcErrorKind.Domain
                 && !rpcException.IsHostAuthenticated
                 && WorkerJson.IsSafeErrorCode(rpcException.Error.Code))
        {
            kind = "domain";
            code = rpcException.Error.Code;
            message = WorkerDiagnosticSink.Sanitize(rpcException.Error.Message, 512);
            if (message.Length == 0) message = "The provider rejected the request.";
        }
        else
        {
            kind = "provider-fault";
            code = "rpc.provider.handler-fault";
            message = "The process provider handler failed.";
            _diagnostics.Write("Process provider handler failed", exception);
            if (_protocol.UsesV2Lifecycle)
            {
                try
                {
                    await _client.WriteProviderFaultDiagnosticAsync(
                        invocation.Id,
                        invocation.Context.Provider.ProviderId,
                        invocation.ServiceId,
                        invocation.MethodId,
                        exception,
                        invocation.Cancellation.Token).ConfigureAwait(false);
                }
                catch (WorkerProtocolException)
                {
                    throw;
                }
                catch (Exception diagnosticException)
                {
                    _diagnostics.Write("Process provider-fault diagnostic failed", diagnosticException);
                }
            }
        }

        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.error");
            writer.WriteString("id", invocation.Id);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
    }

    private void HandleInvocationCancellation(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "id");
        var id = WorkerJson.RequiredSafeId(root, "id");
        InboundInvocation? invocation;
        lock (Gate)
        {
            if (!_invocations.TryGetValue(id, out invocation))
            {
                // A terminal response and the Host cancellation can cross on independent streams.
                if (_rememberedHostIds.Contains(id)) return;
                throw new WorkerProtocolException(
                    $"Host sent an unsolicited invocation cancellation for '{id}'.");
            }
            if (invocation.HostCancelled)
            {
                throw new WorkerProtocolException(
                    $"Host sent a duplicate invocation cancellation for '{id}'.");
            }
            invocation.HostCancelled = true;
        }
        CancelInvocation(invocation);
    }

    private void BeginShutdown(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "shutdownId", "reason");
        var shutdownId = WorkerJson.RequiredSafeId(root, "shutdownId");
        var reason = WorkerJson.RequiredString(root, "reason", 128);
        InboundInvocation[] invocations;
        lock (Gate)
        {
            var canStop = _protocol.UsesV2Lifecycle
                ? _phase is RuntimePhase.Ready
                    or RuntimePhase.CandidateStarting
                    or RuntimePhase.CandidateStarted
                    or RuntimePhase.Committing
                    or RuntimePhase.Committed
                    or RuntimePhase.Activating
                    or RuntimePhase.Active
                : _phase is RuntimePhase.Ready or RuntimePhase.Active;
            if (!canStop)
            {
                throw new WorkerProtocolException("host.shutdown is duplicate or out of order.");
            }
            _phase = RuntimePhase.Stopping;
            invocations = _invocations.Values.ToArray();
        }

        CancelSource(_workStop);
        foreach (var invocation in invocations) CancelInvocation(invocation);
        var clientDrain = _client.CancelAllForShutdownAsync();
        _shutdownOperation = CompleteShutdownAsync(
            shutdownId,
            reason,
            invocations,
            clientDrain);
    }

    private async Task CompleteShutdownAsync(
        string shutdownId,
        string reason,
        IReadOnlyList<InboundInvocation> invocations,
        Task clientDrain)
    {
        try
        {
            await Task.WhenAll(invocations.Select(static invocation => invocation.Operation)).ConfigureAwait(false);
            await _candidateStartOperation.ConfigureAwait(false);
            await _generationCommitOperation.ConfigureAwait(false);
            await _activationOperation.ConfigureAwait(false);
            await clientDrain.ConfigureAwait(false);
            ThrowIfFaulted();

            if (Configuration.OnShutdown is not null)
            {
                await Configuration.OnShutdown(
                    new SunderWorkerShutdownContext(reason),
                    _protocolStop.Token).ConfigureAwait(false);
            }
            lock (Gate)
            {
                _loggingClosed = true;
            }
            await _client.DrainLoggingForShutdownAsync().ConfigureAwait(false);

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.shutdown-ack");
                writer.WriteString("shutdownId", shutdownId);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                _phase = RuntimePhase.Stopped;
            }
            CancelSource(_protocolStop);
        }
        catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker shutdown sequence failed.", exception));
        }
    }

    private byte[] SerializeResult(string type, string id, JsonElement value)
        => WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("id", id);
            writer.WritePropertyName("value");
            WorkerJson.WriteJsonValue(writer, value);
            writer.WriteEndObject();
        });

    private bool ShouldSuppressResponse(InboundInvocation invocation)
    {
        lock (Gate)
        {
            return (_phase is RuntimePhase.Stopping or RuntimePhase.Stopped or RuntimePhase.Faulted)
                   && !invocation.HostCancelled;
        }
    }

    private void RememberHostId(string id)
    {
        _rememberedHostIds.Add(id);
        _rememberedHostIdOrder.Enqueue(id);
        while (_rememberedHostIdOrder.Count > _limits.MaximumRememberedHostIds)
        {
            _rememberedHostIds.Remove(_rememberedHostIdOrder.Dequeue());
        }
    }

    private void CancelFromCaller()
    {
        InboundInvocation[] invocations;
        lock (Gate)
        {
            if (_phase is RuntimePhase.Stopped or RuntimePhase.Faulted) return;
            _phase = RuntimePhase.Stopping;
            invocations = _invocations.Values.ToArray();
        }
        CancelSource(_workStop);
        foreach (var invocation in invocations) CancelInvocation(invocation);
        _client.FailAll(new OperationCanceledException("The worker run was cancelled."));
        CancelSource(_protocolStop);
    }

    private void CompleteRuntime()
    {
        CancelSource(_workStop);
        InboundInvocation[] invocations;
        lock (Gate)
        {
            invocations = _invocations.Values.ToArray();
        }
        foreach (var invocation in invocations) CancelInvocation(invocation);
        _client.FailAll(_failure ?? new OperationCanceledException("The worker runtime has stopped."));
        CancelSource(_protocolStop);
    }

    private bool IsStopping()
    {
        lock (Gate)
        {
            return _phase is RuntimePhase.Stopping or RuntimePhase.Stopped or RuntimePhase.Faulted;
        }
    }

    private void ThrowIfFaulted()
    {
        lock (Gate)
        {
            ThrowIfFaultedUnderGate();
        }
    }

    private void ThrowIfFaultedUnderGate()
    {
        if (_failure is not null) throw _failure;
    }

    private void CancelInvocation(InboundInvocation invocation)
    {
        try
        {
            invocation.Cancellation.Cancel();
        }
        catch (AggregateException exception)
        {
            _diagnostics.Write("Provider cancellation callback failed", exception);
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with an advisory cancellation.
        }
    }

    private static void CancelSource(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    private enum RuntimePhase
    {
        WaitingForHello,
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

    private enum InboundInvocationKind
    {
        Unary,
        ServerStream,
    }

    private sealed class InboundInvocation(
        string id,
        InboundInvocationKind kind,
        CancellationTokenSource cancellation,
        WorkerInvocationAuthority authority,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId)
    {
        public string Id { get; } = id;
        public InboundInvocationKind Kind { get; } = kind;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public WorkerInvocationAuthority Authority { get; } = authority;
        public SunderRpcInvocationContext Context { get; } = context;
        public string ServiceId { get; } = serviceId;
        public string MethodId { get; } = methodId;
        public Task Operation { get; set; } = Task.CompletedTask;
        public bool HostCancelled { get; set; }
    }
}
