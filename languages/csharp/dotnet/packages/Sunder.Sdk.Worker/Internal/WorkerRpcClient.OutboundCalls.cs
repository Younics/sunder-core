using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
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
                    if (!call.Cancelled)
                    {
                        var queuedValue = WorkerJson.CloneValueForQueue(value, _limits, out var bytes);
                        if (call.Events.TryWrite(queuedValue, bytes) is { } rejection)
                        {
                            call.Cancelled = true;
                            var exhausted = StreamQueueResourceExhausted(call, rejection);
                            call.UnaryCompletion.TrySetException(exhausted);
                            call.Events.Fail(exhausted);
                            _ = SendCancellationAsync(call);
                        }
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
                call.Events.Complete();
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
                    call.Events.Fail(error!);
                }
                break;
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
        Action? started = null,
        bool migrationPreparationRead = false)
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
                started,
                migrationPreparationRead,
                streamIdentity: null);
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
        OutboundStreamIdentity streamIdentity,
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
            started: null,
            migrationPreparationRead: false,
            streamIdentity: streamIdentity);
        try
        {
            await call.RequestWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var value in call.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
        Action? started,
        bool migrationPreparationRead,
        OutboundStreamIdentity? streamIdentity)
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
            streamIdentity,
            _limits.MaximumStreamQueueMessages,
            _limits.MaximumStreamQueueBytes,
            cancellationToken);
        lock (_runtime.Gate)
        {
            if (control
                    ? !_runtime.CanSendControlUnderGate
                    : logging
                        ? !_runtime.CanSendLoggingUnderGate
                        : migrationPreparationRead
                            ? !_runtime.CanReadStateForMigrationPreparationUnderGate
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
            try
            {
                call.RequestWrite = _output.WriteAsync(body).AsTask();
                started?.Invoke();
            }
            catch (WorkerTransportResourceException)
            {
                _calls.Remove(id);
                call.Terminal = true;
                call.TerminalCompletion.TrySetResult();
                throw RequestResourceExhausted("rpc.worker.write-queue-limit", "The worker protocol write queue is full.");
            }
            catch
            {
                _calls.Remove(id);
                call.Terminal = true;
                call.TerminalCompletion.TrySetResult();
                throw;
            }
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
            lock (_runtime.Gate)
            {
                if (_calls.Remove(call.Id)) call.Terminal = true;
            }
            call.CancellationRegistration.Dispose();
            call.ReleaseTerminalResource();
            call.TerminalCompletion.TrySetResult();
            call.UnaryCompletion.TrySetException(exception);
            call.Events.Fail(exception);
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
                call.Events.Fail(new OperationCanceledException(cancellationToken));
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
        catch (WorkerTransportResourceException)
        {
            throw RequestResourceExhausted(
                "rpc.worker.request-limit",
                "The worker RPC request exceeds the negotiated transport limits.");
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

    private void WriteParentInvocationId(Utf8JsonWriter writer)
    {
        if (!_environment.RpcExtendedDeadlineV1) return;
        if (_runtime.CurrentInvocationId is { } invocationId)
        {
            writer.WriteString("parentInvocationId", invocationId);
        }
        else
        {
            writer.WriteNull("parentInvocationId");
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

    private static SunderRpcException RequestResourceExhausted(string code, string message)
        => new(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            code,
            message,
            SunderRpcDispatchState.NotDispatched,
            SunderRpcRetrySafety.Safe));

    private static SunderRpcException StreamQueueResourceExhausted(
        OutboundCall call,
        WorkerJsonEventQueueRejection rejection)
    {
        var identity = call.StreamIdentity ?? OutboundStreamIdentity.Stream;
        var message = FormattableString.Invariant(
            $"The worker RPC stream queue exceeded its count or byte bound. Local diagnostic: limit={FormatQueueLimit(rejection.Limit)}; queuedMessages={rejection.QueuedMessages}/{rejection.MaximumMessages}; queuedBytes={rejection.QueuedBytes}/{rejection.MaximumBytes}; incomingBytes={rejection.IncomingBytes}; operation={identity.Operation}");
        if (identity.ServiceId is not null) message += $"; service={identity.ServiceId}";
        if (identity.MethodId is not null) message += $"; method={identity.MethodId}";
        message += ".";
        if (message.Length > 512) message = message[..512];
        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.stream-queue-limit",
            message,
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe));
    }

    private static string FormatQueueLimit(WorkerJsonEventQueueLimit limit)
        => limit switch
        {
            WorkerJsonEventQueueLimit.Count => "count",
            WorkerJsonEventQueueLimit.Bytes => "bytes",
            WorkerJsonEventQueueLimit.Count | WorkerJsonEventQueueLimit.Bytes => "count+bytes",
            _ => "unknown",
        };

    private static string SanitizeStreamIdentity(string value)
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
        OutboundStreamIdentity? streamIdentity,
        int streamCapacity,
        long streamQueueBytes,
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
        public OutboundStreamIdentity? StreamIdentity { get; } = streamIdentity;
        public bool HasTerminalResourceRelease => Volatile.Read(ref _terminalResourceRelease) is not null;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Task RequestWrite { get; set; } = Task.CompletedTask;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public TaskCompletionSource<JsonElement> UnaryCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerJsonEventQueue Events { get; } = new(streamCapacity, streamQueueBytes);
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }

        public void ReleaseTerminalResource()
            => Interlocked.Exchange(ref _terminalResourceRelease, null)?.Invoke();

        public void CompleteCancellation()
        {
            UnaryCompletion.TrySetCanceled(CancellationToken);
            Events.Fail(new OperationCanceledException(CancellationToken));
        }
    }

    private readonly record struct OutboundStreamIdentity(
        string Operation,
        string? ServiceId,
        string? MethodId)
    {
        public static OutboundStreamIdentity Stream { get; } = new("stream", null, null);
        public static OutboundStreamIdentity Watch { get; } = new("watch", null, null);

        public static OutboundStreamIdentity Subscribe(string serviceId, string methodId)
            => new(
                "subscribe",
                SanitizeStreamIdentity(serviceId),
                SanitizeStreamIdentity(methodId));
    }
}
