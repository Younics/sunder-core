using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient : ISunderRpcClient
{
    private readonly SunderWorkerRuntime _runtime;
    private readonly WorkerProtocolOutput _output;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly Dictionary<string, OutboundCall> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRpcCallScope> _scopes = new(StringComparer.Ordinal);
    private long _nextId;
    private int _pendingScopeOpens;
    private int _closingScopes;
    private int _openContentHandles;

    public WorkerRpcClient(
        SunderWorkerRuntime runtime,
        WorkerProtocolOutput output,
        WorkerEnvironment environment,
        WorkerLimits limits)
    {
        _runtime = runtime;
        _output = output;
        _environment = environment;
        _limits = limits;
    }

    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.UsesV2Protocol)
        {
            throw new NotSupportedException(
                "Caller-owned RPC call scopes are not supported by sunder.worker.v1.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var capacity = ReserveCallScopeCapacity();
        var capacityReleased = false;
        WorkerRpcCallScope? acceptedScope = null;

        try
        {
            await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "worker.scope-open");
                    writer.WriteString("id", id);
                    WriteDeadline(writer, options);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                terminalResourceRelease: capacity.Release,
                abandonedResult: AbandonCallScopeResultAsync,
                resultConsumer: value =>
                {
                    var identity = ParseHostValue(value, WorkerWire.ReadCallScopeIdentity);
                    var scope = new WorkerRpcCallScope(
                        this,
                        _environment,
                        _limits,
                        identity.ScopeId,
                        identity.DeadlineUtc);
                    WorkerProtocolException? protocolFailure = null;
                    lock (_runtime.Gate)
                    {
                        EnsureActiveUnderGate();
                        if (!_scopes.TryAdd(identity.ScopeId, scope))
                        {
                            protocolFailure = new WorkerProtocolException(
                                $"Host reused RPC call-scope id '{identity.ScopeId}'.");
                        }
                    }
                    if (protocolFailure is not null)
                    {
                        _runtime.Fail(protocolFailure);
                        throw protocolFailure;
                    }
                    scope.ArmDeadline();
                    acceptedScope = scope;
                    capacity.Release();
                    capacityReleased = true;
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            return acceptedScope!;
        }
        finally
        {
            if (!capacityReleased) capacity.Release();
        }
    }

    public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => await GetProviderAsync(scopeId: null, endpoint, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        string? scopeId,
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.get-provider");
                writer.WriteString("id", id);
                WriteScopeId(writer, scopeId);
                writer.WriteString("endpointReference", endpoint.Value);
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return ParseHostValue(value, WorkerWire.ReadProviderSnapshot);
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => TryReportInvariantViolationAsync(scopeId: null, endpoint, exception, cancellationToken);

    internal async ValueTask<bool> TryReportInvariantViolationAsync(
        string? scopeId,
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!_runtime.UsesV2Protocol)
        {
            throw new NotSupportedException(
                "Provider invariant reporting is not supported by sunder.worker.v1.");
        }
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exception);
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.report-invariant-violation");
                writer.WriteString("id", id);
                WriteScopeId(writer, scopeId);
                writer.WriteString("endpointReference", endpoint.Value);
                writer.WriteString("exceptionMessage", SanitizeExceptionMessage(exception.Message));
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
        return ParseHostValue(value, static result => result.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new WorkerProtocolException("Host invariant-report result must be a Boolean."),
        });
    }

    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
        => await DiscoverAsync(scopeId: null, contractId, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string? scopeId,
        string contractId,
        CancellationToken cancellationToken)
    {
        ValidateBoundedString(contractId, nameof(contractId), 256);
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.discover");
                writer.WriteString("id", id);
                WriteScopeId(writer, scopeId);
                writer.WriteString("contractId", contractId);
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
        return ParseHostValue(value, WorkerWire.ReadCatalogSnapshot);
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in WatchAsync(
                           scopeId: null,
                           afterRevision,
                           afterSequence,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        string? scopeId,
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        await foreach (var value in StreamCallAsync(
                           id => Serialize(writer =>
                           {
                               writer.WriteStartObject();
                                writer.WriteString("type", "worker.watch");
                                writer.WriteString("id", id);
                                WriteScopeId(writer, scopeId);
                                writer.WriteNumber("afterRevision", afterRevision);
                               writer.WriteNumber("afterSequence", afterSequence);
                               writer.WriteEndObject();
                           }),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return ParseHostValue(value, WorkerWire.ReadCatalogEvent);
        }
    }

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(
            scopeId: null,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            cancellationToken);

    internal ValueTask<JsonElement> InvokeAsync(
        string? scopeId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateBoundedString(serviceId, nameof(serviceId), 128);
        ValidateBoundedString(methodId, nameof(methodId), 128);
        ValidateJsonElement(request, nameof(request));
        return UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.invoke");
                writer.WriteString("id", id);
                WriteScopeId(writer, scopeId);
                writer.WriteString("endpointReference", endpoint.Value);
                writer.WriteString("serviceId", serviceId);
                writer.WriteString("methodId", methodId);
                writer.WritePropertyName("request");
                WorkerJson.WriteJsonValue(writer, request);
                WriteDeadline(writer, options);
                writer.WriteEndObject();
            }),
            cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => SubscribeAsync(
            scopeId: null,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            cancellationToken);

    internal IAsyncEnumerable<JsonElement> SubscribeAsync(
        string? scopeId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateBoundedString(serviceId, nameof(serviceId), 128);
        ValidateBoundedString(methodId, nameof(methodId), 128);
        ValidateJsonElement(request, nameof(request));
        return StreamCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.subscribe");
                writer.WriteString("id", id);
                WriteScopeId(writer, scopeId);
                writer.WriteString("endpointReference", endpoint.Value);
                writer.WriteString("serviceId", serviceId);
                writer.WriteString("methodId", methodId);
                writer.WritePropertyName("request");
                WorkerJson.WriteJsonValue(writer, request);
                WriteDeadline(writer, options);
                writer.WriteEndObject();
            }),
            cancellationToken);
    }

    internal ValueTask<JsonElement> RegisterContentFileAsync(
        string invocationId,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
        => UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.content-register");
                writer.WriteString("id", id);
                writer.WriteString("invocationId", invocationId);
                writer.WriteString("filePath", filePath);
                writer.WritePropertyName("options");
                WorkerWire.WriteContentOptions(writer, options);
                writer.WriteEndObject();
            }),
            cancellationToken);

    internal ValueTask<JsonElement> RegisterScopeContentFileAsync(
        string scopeId,
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
        => UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.content-register");
                writer.WriteString("id", id);
                writer.WriteString("scopeId", scopeId);
                writer.WriteString("endpointReference", endpoint.Value);
                writer.WriteString("filePath", filePath);
                writer.WritePropertyName("options");
                WorkerWire.WriteContentOptions(writer, options);
                writer.WriteEndObject();
            }),
            cancellationToken);

    internal ValueTask<JsonElement> OpenContentAsync(
        string invocationId,
        SunderRpcContentReference reference,
        Func<bool> authorityActive,
        Func<JsonElement, ValueTask> resultConsumer,
        WorkerResourceReservation reservation,
        CancellationToken cancellationToken)
        => UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.content-open");
                writer.WriteString("id", id);
                writer.WriteString("invocationId", invocationId);
                writer.WritePropertyName("reference");
                WorkerWire.WriteContentReference(writer, reference);
                writer.WriteEndObject();
            }),
            cancellationToken,
            terminalResourceRelease: reservation.Release,
            abandonedResult: value => AbandonInvocationContentResultAsync(
                invocationId,
                reference,
                authorityActive,
                value),
            resultConsumer: resultConsumer);

    internal ValueTask<JsonElement> OpenScopeContentAsync(
        string scopeId,
        SunderRpcContentReference reference,
        Func<bool> authorityActive,
        Func<string, ValueTask> abandonHandle,
        Func<JsonElement, ValueTask> resultConsumer,
        WorkerResourceReservation reservation,
        CancellationToken cancellationToken)
        => UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.content-open");
                writer.WriteString("id", id);
                writer.WriteString("scopeId", scopeId);
                writer.WritePropertyName("reference");
                WorkerWire.WriteContentReference(writer, reference);
                writer.WriteEndObject();
            }),
            cancellationToken,
            terminalResourceRelease: reservation.Release,
            abandonedResult: value => AbandonScopeContentResultAsync(
                reference,
                authorityActive,
                abandonHandle,
                value),
            resultConsumer: resultConsumer);

    internal ValueTask ReleaseInvocationContentAsync(
        string invocationId,
        string handleId,
        CancellationToken cancellationToken)
        => ReleaseContentAsync(
            _runtime.UsesV2Protocol ? "worker.content-release" : "worker.content-discard",
            "invocationId",
            invocationId,
            handleId,
            cancellationToken);

    internal ValueTask ReleaseScopeContentAsync(
        string scopeId,
        string handleId,
        CancellationToken cancellationToken)
        => ReleaseContentAsync(
            "worker.content-release",
            "scopeId",
            scopeId,
            handleId,
            cancellationToken);

    internal async Task CloseScopeAsync(
        WorkerRpcCallScope scope,
        WorkerContentReadStream[] streams)
    {
        var shouldClose = false;
        lock (_runtime.Gate)
        {
            if (_scopes.TryGetValue(scope.ScopeId, out var registered)
                && ReferenceEquals(registered, scope))
            {
                _scopes.Remove(scope.ScopeId);
                shouldClose = _runtime.CanSendControlUnderGate;
                if (shouldClose)
                {
                    _closingScopes++;
                }
            }
        }

        try
        {
            if (shouldClose)
            {
                await CloseScopeIdAsync(scope.ScopeId).ConfigureAwait(false);
            }
        }
        finally
        {
            if (shouldClose)
            {
                lock (_runtime.Gate)
                {
                    _closingScopes--;
                }
            }
            ReleaseContentReservations(streams);
        }
    }

    private async ValueTask ReleaseContentAsync(
        string type,
        string authorityProperty,
        string authorityId,
        string handleId,
        CancellationToken cancellationToken)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteString("id", id);
                writer.WriteString(authorityProperty, authorityId);
                writer.WriteString("handleId", handleId);
                writer.WriteEndObject();
            }),
            cancellationToken,
            control: true,
            awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
        EnsureNullResult(value, "Host content release result must be null.");
    }

    private async ValueTask CloseScopeIdAsync(string scopeId)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.scope-close");
                writer.WriteString("id", id);
                writer.WriteString("scopeId", scopeId);
                writer.WriteEndObject();
            }),
            CancellationToken.None,
            control: true).ConfigureAwait(false);
        EnsureNullResult(value, "Host call-scope close result must be null.");
    }

    private static void ReleaseContentReservations(IEnumerable<WorkerContentReadStream> streams)
    {
        foreach (var stream in streams) stream.ReleaseReservation();
    }

    internal void FailScopeCleanup(Exception exception)
    {
        lock (_runtime.Gate)
        {
            if (!_runtime.CanSendControlUnderGate) return;
        }
        _runtime.Fail(exception is WorkerProtocolException
            ? exception
            : new WorkerProtocolException(
                "Worker could not close an RPC call scope.",
                exception));
    }

    private async ValueTask AbandonCallScopeResultAsync(JsonElement value)
    {
        var identity = ParseHostValue(value, WorkerWire.ReadCallScopeIdentity);
        lock (_runtime.Gate)
        {
            if (_scopes.ContainsKey(identity.ScopeId))
            {
                throw new WorkerProtocolException(
                    $"Host reused RPC call-scope id '{identity.ScopeId}'.");
            }
            if (!_runtime.CanSendControlUnderGate) return;
        }
        await CloseScopeIdAsync(identity.ScopeId).ConfigureAwait(false);
    }

    private async ValueTask AbandonInvocationContentResultAsync(
        string invocationId,
        SunderRpcContentReference reference,
        Func<bool> authorityActive,
        JsonElement value)
    {
        var handle = ParseHostValue(value, WorkerWire.ReadContentFileHandle);
        if (!ValidateAbandonedContentHandle(handle, reference, authorityActive)) return;
        if (!authorityActive() || !IsActive()) return;
        await ReleaseInvocationContentAsync(
            invocationId,
            handle.HandleId,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask AbandonScopeContentResultAsync(
        SunderRpcContentReference reference,
        Func<bool> authorityActive,
        Func<string, ValueTask> abandonHandle,
        JsonElement value)
    {
        var handle = ParseHostValue(value, WorkerWire.ReadContentFileHandle);
        var valid = ValidateAbandonedContentHandle(handle, reference, authorityActive);
        if (!valid && authorityActive()) return;
        await abandonHandle(handle.HandleId).ConfigureAwait(false);
    }

    internal void ReleaseInvocationContentDetached(
        string invocationId,
        string handleId,
        WorkerResourceReservation reservation)
        => _ = ObserveDetachedReleaseAsync(
            () => ReleaseInvocationContentAsync(
                invocationId,
                handleId,
                CancellationToken.None),
            reservation);

    internal void ReleaseScopeContentDetached(
        string scopeId,
        string handleId,
        WorkerResourceReservation reservation)
        => _ = ObserveDetachedReleaseAsync(
            () => ReleaseScopeContentAsync(
                scopeId,
                handleId,
                CancellationToken.None),
            reservation);

    private bool ValidateAbandonedContentHandle(
        WorkerContentFileHandle handle,
        SunderRpcContentReference reference,
        Func<bool> authorityActive)
    {
        var filePath = WorkerInvocationAuthority.ResolveHostContentFile(_environment, handle.FilePath);
        try
        {
            var information = new FileInfo(filePath);
            if (!information.Exists)
            {
                if (!authorityActive()) return false;
                throw new WorkerProtocolException("Host RPC content file does not exist.");
            }
            if (information.Length != reference.Length)
            {
                throw new WorkerProtocolException(
                    "Host RPC content file length does not match its reference.");
            }
            return true;
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!authorityActive()) return false;
            throw new WorkerProtocolException(
                "Host RPC content file could not be validated safely.",
                exception);
        }
    }

    private async Task ObserveDetachedReleaseAsync(
        Func<ValueTask> release,
        WorkerResourceReservation reservation)
    {
        try
        {
            await release().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtime.StoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_runtime.Gate)
            {
                if (!_runtime.IsActiveUnderGate) return;
            }
            _runtime.Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException(
                    "Worker could not release a Host RPC content handle.",
                    exception));
        }
        finally
        {
            reservation.Release();
        }
    }

    internal WorkerResourceReservation ReserveContentHandle()
    {
        lock (_runtime.Gate)
        {
            EnsureActiveUnderGate();
            if (_openContentHandles >= _limits.MaximumContentHandles)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.content-handle-limit",
                    "The worker RPC content-handle limit was reached.");
            }
            _openContentHandles++;
        }
        return new WorkerResourceReservation(ReleaseContentHandle);
    }

    private void ReleaseContentHandle()
    {
        lock (_runtime.Gate)
        {
            if (_openContentHandles > 0) _openContentHandles--;
        }
    }

    private WorkerResourceReservation ReserveCallScopeCapacity()
    {
        lock (_runtime.Gate)
        {
            EnsureActiveUnderGate();
            if (_scopes.Count + _pendingScopeOpens + _closingScopes >= _limits.MaximumCallScopes)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.worker.scope-limit",
                    "The worker RPC call-scope limit was reached.");
            }
            _pendingScopeOpens++;
        }
        return new WorkerResourceReservation(() =>
        {
            lock (_runtime.Gate)
            {
                _pendingScopeOpens--;
            }
        });
    }

    internal void HandleResponse(string type, JsonElement root)
    {
        var allowed = type switch
        {
            "host.result" or "host.event" => new[] { "type", "id", "value" },
            "host.complete" => new[] { "type", "id" },
            _ => new[] { "type", "id", "error" },
        };
        WorkerJson.RequireOnlyProperties(root, allowed);
        var id = WorkerJson.RequiredSafeId(root, "id");
        var value = type is "host.result" or "host.event"
            ? WorkerJson.RequiredValue(root, "value").Clone()
            : default;
        var error = type == "host.error"
            ? WorkerWire.ReadHostError(WorkerJson.RequiredValue(root, "error"))
            : null;

        OutboundCall call;
        var consumeResult = false;
        lock (_runtime.Gate)
        {
            if (!_calls.TryGetValue(id, out call!) || call.Terminal)
            {
                throw new WorkerProtocolException(
                    $"Host sent an unsolicited, duplicate, or post-terminal response for '{id}'.");
            }

            switch (type)
            {
                case "host.event":
                    if (call.Kind != OutboundCallKind.Stream)
                    {
                        throw new WorkerProtocolException("Host sent an event for a unary worker call.");
                    }
                    if (!call.Cancelled && !call.Events.Writer.TryWrite(value))
                    {
                        throw new WorkerProtocolException("Worker stream event queue exceeded its bound.");
                    }
                    return;
                case "host.result":
                    if (call.Kind != OutboundCallKind.Unary)
                    {
                        throw new WorkerProtocolException("Host sent a unary result for a stream worker call.");
                    }
                    break;
                case "host.complete":
                    if (call.Kind != OutboundCallKind.Stream)
                    {
                        throw new WorkerProtocolException("Host completed a unary worker call without a result.");
                    }
                    break;
                case "host.error":
                    break;
                default:
                    throw new WorkerProtocolException($"Unknown Host response type '{type}'.");
            }

            call.Terminal = true;
            consumeResult = !call.Cancelled
                            && type == "host.result"
                            && call.ResultConsumer is not null;
            var abandonResult = call.Cancelled
                                && !call.AwaitHostTerminalAfterCancellation
                                && type == "host.result"
                                && call.AbandonedResult is not null;
            if (!consumeResult && !abandonResult)
            {
                _calls.Remove(id);
                call.TerminalCompletion.TrySetResult();
            }
        }

        call.CancellationRegistration.Dispose();
        if (call.Cancelled && !call.AwaitHostTerminalAfterCancellation)
        {
            if (type == "host.result" && call.AbandonedResult is not null)
            {
                _ = ObserveAbandonedResultAsync(call, value);
            }
            else
            {
                call.ReleaseTerminalResource();
                call.CompleteCancellation();
            }
            return;
        }
        if (consumeResult)
        {
            _ = ObserveResultConsumerAsync(call, value);
            return;
        }
        switch (type)
        {
            case "host.result":
                call.UnaryCompletion.TrySetResult(value);
                break;
            case "host.complete":
                call.Events.Writer.TryComplete();
                break;
            default:
                call.ReleaseTerminalResource();
                if (call.Cancelled && error!.Error.Kind == SunderRpcErrorKind.Cancelled)
                {
                    call.CompleteCancellation();
                }
                else
                {
                    call.UnaryCompletion.TrySetException(error!);
                    call.Events.Writer.TryComplete(error);
                }
                break;
        }
    }

    internal async Task CancelAllForShutdownAsync()
    {
        OutboundCall[] calls;
        WorkerRpcCallScope[] scopes;
        lock (_runtime.Gate)
        {
            scopes = _scopes.Values.ToArray();
            calls = _calls.Values
                .Where(static call => !call.Control && !call.Logging && !call.Terminal && !call.Cancelled)
                .ToArray();
        }
        foreach (var call in calls)
        {
            Cancel(call, new CancellationToken(canceled: true));
        }
        var scopeCleanup = scopes.Select(static scope => scope.RevokeFromRuntime()).ToArray();
        Task[] terminals;
        lock (_runtime.Gate)
        {
            terminals = _calls.Values
                .Select(static call => call.TerminalCompletion.Task)
                .ToArray();
        }
        await Task.WhenAll(terminals.Concat(scopeCleanup)).ConfigureAwait(false);

        WorkerPayloadHandle[] payloads;
        lock (_runtime.Gate)
        {
            payloads = _payloadHandles.Values.ToArray();
            _payloadHandles.Clear();
            foreach (var payload in payloads) RememberPayloadHandleUnderGate(payload.Id);
        }
        await Task.WhenAll(payloads.Select(ReleasePayloadForShutdownAsync)).ConfigureAwait(false);
    }

    internal Task DrainLoggingForShutdownAsync()
    {
        lock (_runtime.Gate)
        {
            return Task.WhenAll(_calls.Values
                .Where(static call => call.Logging && !call.Terminal)
                .Select(static call => call.TerminalCompletion.Task));
        }
    }

    internal void FailAll(Exception exception)
    {
        OutboundCall[] calls;
        WorkerRpcCallScope[] scopes;
        lock (_runtime.Gate)
        {
            scopes = _scopes.Values.ToArray();
            _scopes.Clear();
            calls = _calls.Values.ToArray();
            _calls.Clear();
            foreach (var call in calls) call.Terminal = true;
        }
        foreach (var scope in scopes) _ = scope.RevokeFromRuntime();
        foreach (var call in calls)
        {
            call.CancellationRegistration.Dispose();
            call.ReleaseTerminalResource();
            call.TerminalCompletion.TrySetResult();
            call.UnaryCompletion.TrySetException(exception);
            call.Events.Writer.TryComplete(exception);
        }
    }

    private async ValueTask<JsonElement> UnaryCallAsync(
        Func<string, byte[]> createBody,
        CancellationToken cancellationToken,
        bool control = false,
        Action? terminalResourceRelease = null,
        Func<JsonElement, ValueTask>? abandonedResult = null,
        Func<JsonElement, ValueTask>? resultConsumer = null,
        bool logging = false,
        bool awaitHostTerminalAfterCancellation = false,
        Action? started = null)
    {
        OutboundCall call;
        try
        {
            call = StartCall(
                OutboundCallKind.Unary,
                createBody,
                cancellationToken,
                control,
                terminalResourceRelease,
                abandonedResult,
                resultConsumer,
                logging,
                awaitHostTerminalAfterCancellation,
                started);
        }
        catch
        {
            terminalResourceRelease?.Invoke();
            throw;
        }
        if (terminalResourceRelease is null && !awaitHostTerminalAfterCancellation)
        {
            await call.RequestWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await call.RequestWrite.ConfigureAwait(false);
        }
        return await call.UnaryCompletion.Task.ConfigureAwait(false);
    }

    private async IAsyncEnumerable<JsonElement> StreamCallAsync(
        Func<string, byte[]> createBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = StartCall(
            OutboundCallKind.Stream,
            createBody,
            cancellationToken,
            control: false,
            terminalResourceRelease: null,
            abandonedResult: null,
            resultConsumer: null,
            logging: false,
            awaitHostTerminalAfterCancellation: false,
            started: null);
        try
        {
            await call.RequestWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var value in call.Events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return value;
            }
        }
        finally
        {
            Cancel(call, cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
    }

    private OutboundCall StartCall(
        OutboundCallKind kind,
        Func<string, byte[]> createBody,
        CancellationToken cancellationToken,
        bool control,
        Action? terminalResourceRelease,
        Func<JsonElement, ValueTask>? abandonedResult,
        Func<JsonElement, ValueTask>? resultConsumer,
        bool logging,
        bool awaitHostTerminalAfterCancellation,
        Action? started)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _nextId);
        if (sequence <= 0)
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.worker.message-id-limit",
                "The worker exhausted its outbound message identifier space.");
        }
        var id = "w" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = createBody(id);
        var call = new OutboundCall(
            id,
            kind,
            control,
            terminalResourceRelease,
            abandonedResult,
            resultConsumer,
            logging,
            awaitHostTerminalAfterCancellation,
            _limits.MaximumStreamQueueMessages,
            cancellationToken);
        lock (_runtime.Gate)
        {
            if (control
                    ? !_runtime.CanSendControlUnderGate
                    : logging
                        ? !_runtime.CanSendLoggingUnderGate
                        : !_runtime.IsActiveUnderGate)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.worker.not-active",
                    "The process worker activation is not active.");
            }
            var laneCount = _calls.Values.Count(item =>
                item.Control == control
                && item.Logging == logging
                && !item.Terminal);
            var laneLimit = control
                ? _limits.MaximumControlCalls
                : logging
                    ? _limits.MaximumLoggingCalls
                    : _limits.MaximumOutboundCalls;
            if (laneCount >= laneLimit)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.ResourceExhausted,
                    control
                        ? "rpc.worker.control-call-limit"
                        : logging
                            ? "rpc.worker.logging-call-limit"
                            : "rpc.worker.call-limit",
                    control
                        ? "The worker RPC control-call limit was reached."
                        : logging
                            ? "The worker logging-call limit was reached."
                        : "The worker RPC call limit was reached.");
            }

            _calls.Add(id, call);
            started?.Invoke();
            call.RequestWrite = _output.WriteAsync(body).AsTask();
            if (cancellationToken.CanBeCanceled)
            {
                call.CancellationRegistration = cancellationToken.UnsafeRegister(
                    static state =>
                    {
                        var (client, outboundCall) = ((WorkerRpcClient, OutboundCall))state!;
                        client.Cancel(outboundCall, outboundCall.CancellationToken);
                    },
                    (this, call));
            }
        }
        _ = ObserveRequestWriteAsync(call);
        return call;
    }

    private async Task ObserveRequestWriteAsync(OutboundCall call)
    {
        try
        {
            await call.RequestWrite.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtime.StoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _runtime.Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("Worker RPC request write failed.", exception));
        }
    }

    private async Task ObserveAbandonedResultAsync(OutboundCall call, JsonElement value)
    {
        try
        {
            await call.AbandonedResult!(value).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtime.StoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_runtime.Gate)
            {
                if (!_runtime.IsActiveUnderGate) return;
            }
            _runtime.Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException(
                    "Worker could not release an abandoned Host RPC resource.",
                    exception));
        }
        finally
        {
            call.ReleaseTerminalResource();
            call.CompleteCancellation();
            lock (_runtime.Gate)
            {
                _calls.Remove(call.Id);
            }
            call.TerminalCompletion.TrySetResult();
        }
    }

    private async Task ObserveResultConsumerAsync(OutboundCall call, JsonElement value)
    {
        try
        {
            await call.ResultConsumer!(value).ConfigureAwait(false);
            call.UnaryCompletion.TrySetResult(value);
        }
        catch (Exception exception)
        {
            call.UnaryCompletion.TrySetException(exception);
        }
        finally
        {
            lock (_runtime.Gate)
            {
                _calls.Remove(call.Id);
            }
            call.TerminalCompletion.TrySetResult();
        }
    }

    private void Cancel(OutboundCall call, CancellationToken cancellationToken)
    {
        lock (_runtime.Gate)
        {
            if (call.Terminal || call.Cancelled || !_calls.ContainsKey(call.Id)) return;
            call.Cancelled = true;
            if (!call.HasTerminalResourceRelease && !call.AwaitHostTerminalAfterCancellation)
            {
                call.UnaryCompletion.TrySetCanceled(cancellationToken);
                call.Events.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            }
        }
        _ = SendCancellationAsync(call);
    }

    private async Task SendCancellationAsync(OutboundCall call)
    {
        try
        {
            await call.RequestWrite.ConfigureAwait(false);
            lock (_runtime.Gate)
            {
                if (call.Terminal || !call.Cancelled || !_calls.ContainsKey(call.Id)) return;
            }
            var body = Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.cancel");
                writer.WriteString("id", call.Id);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtime.StoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _runtime.Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("Worker RPC cancellation write failed.", exception));
        }
    }

    private byte[] Serialize(Action<Utf8JsonWriter> write)
    {
        try
        {
            return WorkerJson.Serialize(_limits, write);
        }
        catch (WorkerProtocolException)
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.worker.frame-limit",
                "The worker RPC request could not be encoded within protocol limits.");
        }
    }

    internal T ParseHostValue<T>(JsonElement value, Func<JsonElement, T> parser)
    {
        try
        {
            return parser(value);
        }
        catch (WorkerProtocolException exception)
        {
            _runtime.Fail(exception);
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            var protocol = new WorkerProtocolException("Host RPC result is malformed.", exception);
            _runtime.Fail(protocol);
            throw protocol;
        }
    }

    internal WorkerProtocolException FailProtocol(WorkerProtocolException exception)
    {
        _runtime.Fail(exception);
        return exception;
    }

    private void EnsureNullResult(JsonElement value, string message)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        var exception = new WorkerProtocolException(message);
        _runtime.Fail(exception);
        throw exception;
    }

    private void EnsureActiveUnderGate()
    {
        if (!_runtime.IsActiveUnderGate)
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.Unavailable,
                "rpc.worker.not-active",
                "The process worker activation is not active.");
        }
    }

    private bool IsActive()
    {
        lock (_runtime.Gate)
        {
            return _runtime.IsActiveUnderGate;
        }
    }

    private static void WriteScopeId(Utf8JsonWriter writer, string? scopeId)
    {
        if (scopeId is not null) writer.WriteString("scopeId", scopeId);
    }

    private static string SanitizeExceptionMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The provider violated an orchestrator invariant.";
        var sanitized = new string(message.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        if (sanitized.Length == 0) return "The provider violated an orchestrator invariant.";
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private static void WriteDeadline(Utf8JsonWriter writer, SunderRpcCallOptions? options)
    {
        if (options?.DeadlineUtc is { } deadline)
        {
            writer.WriteString("deadlineUtc", WorkerJson.FormatUtc(deadline));
        }
        else
        {
            writer.WriteNull("deadlineUtc");
        }
    }

    private static void ValidateBoundedString(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("The value must be a non-empty bounded string.", parameterName);
        }
    }

    private static void ValidateJsonElement(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("The value must contain JSON.", parameterName);
        }
    }

    private enum OutboundCallKind
    {
        Unary,
        Stream,
    }

    private sealed class OutboundCall(
        string id,
        OutboundCallKind kind,
        bool control,
        Action? terminalResourceRelease,
        Func<JsonElement, ValueTask>? abandonedResult,
        Func<JsonElement, ValueTask>? resultConsumer,
        bool logging,
        bool awaitHostTerminalAfterCancellation,
        int streamCapacity,
        CancellationToken cancellationToken)
    {
        private Action? _terminalResourceRelease = terminalResourceRelease;

        public string Id { get; } = id;
        public OutboundCallKind Kind { get; } = kind;
        public bool Control { get; } = control;
        public Func<JsonElement, ValueTask>? AbandonedResult { get; } = abandonedResult;
        public Func<JsonElement, ValueTask>? ResultConsumer { get; } = resultConsumer;
        public bool Logging { get; } = logging;
        public bool AwaitHostTerminalAfterCancellation { get; } = awaitHostTerminalAfterCancellation;
        public bool HasTerminalResourceRelease => Volatile.Read(ref _terminalResourceRelease) is not null;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Task RequestWrite { get; set; } = Task.CompletedTask;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public TaskCompletionSource<JsonElement> UnaryCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<JsonElement> Events { get; } = Channel.CreateBounded<JsonElement>(
            new BoundedChannelOptions(streamCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }

        public void ReleaseTerminalResource()
            => Interlocked.Exchange(ref _terminalResourceRelease, null)?.Invoke();

        public void CompleteCancellation()
        {
            UnaryCompletion.TrySetCanceled(CancellationToken);
            Events.Writer.TryComplete(new OperationCanceledException(CancellationToken));
        }
    }
}

internal sealed class WorkerResourceReservation(Action release)
{
    private Action? _release = release;

    public void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
