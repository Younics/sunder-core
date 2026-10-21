using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker
{
    private readonly Dictionary<string, WorkerContributionCall> _contributionCalls = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, IPackageCallbackHandler> CallbackHandlers { get; private set; }
        = new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase);

    internal IPackageAuthHandler? AuthHandler { get; private set; }

    IPackageAuthHandler? IRuntimePackageWorker.AuthHandler => AuthHandler;

    IPackageCallbackHandler? IRuntimePackageWorker.GetCallbackHandler(string callbackHandlerId)
        => CallbackHandlers.TryGetValue(callbackHandlerId, out var handler) ? handler : null;

    private void ConfigureContributionHandlers(
        IReadOnlyList<string> callbackHandlerIds,
        bool hasAuthHandler)
    {
        var handlers = callbackHandlerIds.ToDictionary(
            static id => id,
            id => (IPackageCallbackHandler)new ProcessWorkerCallbackHandler(this, id),
            StringComparer.OrdinalIgnoreCase);
        if (hasAuthHandler)
        {
            AuthHandler = new ProcessWorkerAuthHandler(this);
            handlers.Add(PackageCallbackHandlerIds.Authentication, AuthHandler);
        }
        CallbackHandlers = handlers;
    }

    private async Task<JsonElement> InvokeContributionAsync(
        string requestType,
        string responseType,
        object value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkerContributionCall call;
        lock (_stateGate)
        {
            ThrowIfFaulted();
            if (_state != WorkerState.Active || _sessionGeneration is null)
            {
                throw new InvalidOperationException(
                    "Process worker contributions are available only for an active Runtime generation.");
            }
            call = new WorkerContributionCall(
                Guid.NewGuid().ToString("N"),
                responseType,
                _sessionGeneration.Value);
            lock (_callGate)
            {
                RequireHostCallCapacityUnderGate(HostCallKind.Unary);
                _contributionCalls.Add(call.Id, call);
            }
            try
            {
                QueueEnvelope(new
                {
                    type = requestType,
                    id = call.Id,
                    activationId = _activationId.ToString("N"),
                    sessionGeneration = call.SessionGeneration,
                    value,
                });
            }
            catch
            {
                lock (_callGate) _contributionCalls.Remove(call.Id);
                throw;
            }
        }

        using var registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var cancellation = (ContributionCancellation)state!;
                cancellation.Worker.CancelContributionCall(cancellation.Call, cancellation.Token);
            },
            new ContributionCancellation(this, call, cancellationToken));
        return await call.Completion.Task.ConfigureAwait(false);
    }

    private void CancelContributionCall(
        WorkerContributionCall call,
        CancellationToken cancellationToken)
    {
        lock (_callGate)
        {
            if (call.Terminal || call.Cancelled || !_contributionCalls.ContainsKey(call.Id)) return;
            call.Cancelled = true;
            call.Completion.TrySetCanceled(cancellationToken);
        }
        TryQueueEnvelope(new
        {
            type = "host.contribution-cancel",
            id = call.Id,
            activationId = _activationId.ToString("N"),
            sessionGeneration = call.SessionGeneration,
        });
        _ = EnforceContributionCancellationDrainAsync(call);
    }

    private async Task EnforceContributionCancellationDrainAsync(WorkerContributionCall call)
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
            if (call.Terminal || !_contributionCalls.ContainsKey(call.Id)) return;
        }
        Fault(new SunderWorkerProtocolException(
            $"Process worker did not terminate cancelled contribution call '{call.Id}' within the cancellation drain deadline."));
    }

    private void HandleContributionResponse(string type, JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "id",
            "activationId",
            "sessionGeneration",
            type == "worker.contribution-error" ? "error" : "value");
        var id = SunderWorkerProtocol.RequiredId(root);
        var activationId = SunderWorkerProtocol.RequiredString(root, "activationId", 64);
        var generation = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration");
        WorkerContributionCall call;
        lock (_callGate)
        {
            if (!_contributionCalls.TryGetValue(id, out call!) || call.Terminal)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent an unsolicited or duplicate contribution response for '{id}'.");
            }
            if (!string.Equals(activationId, _activationId.ToString("N"), StringComparison.Ordinal)
                || generation != call.SessionGeneration
                || type != "worker.contribution-error"
                && !string.Equals(type, call.ResponseType, StringComparison.Ordinal))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker contribution response does not match its exact call and generation.");
            }
        }

        Exception? failure = null;
        JsonElement result = default;
        if (type == "worker.contribution-error")
        {
            var error = SunderWorkerProtocol.RequiredValue(root, "error");
            SunderWorkerProtocol.RequireOnlyProperties(error, "code", "message");
            var code = SunderWorkerProtocol.RequiredString(error, "code", 128);
            if (!code.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker contribution error code contains unsupported characters.");
            }
            failure = new ProcessWorkerContributionException(
                code,
                ReadBoundedString(error, "message", 512));
        }
        else
        {
            result = SunderWorkerProtocol.RequiredValue(root, "value").Clone();
        }

        lock (_callGate)
        {
            call.Terminal = true;
            _contributionCalls.Remove(id);
        }
        if (call.Cancelled) return;
        if (failure is not null)
        {
            call.Completion.TrySetException(failure);
        }
        else
        {
            call.Completion.TrySetResult(result);
        }
    }

    private void FailOutstandingContributionCalls(Exception exception)
    {
        WorkerContributionCall[] calls;
        lock (_callGate)
        {
            calls = _contributionCalls.Values.ToArray();
            _contributionCalls.Clear();
            foreach (var call in calls) call.Terminal = true;
        }
        foreach (var call in calls)
        {
            call.Completion.TrySetException(exception);
        }
    }

    private PackageCallbackStartResult? ReadCallbackStartResult(JsonElement value, string sessionId)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "packageId",
            "callbackSessionId",
            "flow",
            "launchUrl",
            "message");
        RequirePackageIdentity(value);
        var returnedSessionId = SunderWorkerProtocol.RequiredString(value, "callbackSessionId", 128);
        if (!string.Equals(returnedSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker callback result changed the Host-owned session id.");
        }
        var flow = SunderWorkerProtocol.RequiredString(value, "flow", 32) switch
        {
            "browser" => PackageCallbackFlowKind.Browser,
            _ => throw new SunderWorkerProtocolException("Process worker callback flow is unsupported."),
        };
        return new PackageCallbackStartResult(
            _package.PackageId,
            returnedSessionId,
            flow,
            SunderWorkerProtocol.RequiredString(value, "launchUrl", 4096),
            ReadBoundedString(value, "message", 2048));
    }

    private PackageCallbackCompletionResult ReadCallbackCompletionResult(
        JsonElement value,
        string sessionId)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "packageId",
            "callbackSessionId",
            "state",
            "message");
        RequirePackageIdentity(value);
        var returnedSessionId = SunderWorkerProtocol.RequiredString(value, "callbackSessionId", 128);
        if (!string.Equals(returnedSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker callback completion changed the Host-owned session id.");
        }
        var state = SunderWorkerProtocol.RequiredString(value, "state", 32) switch
        {
            "completed" => PackageCallbackCompletionState.Completed,
            "failed" => PackageCallbackCompletionState.Failed,
            "cancelled" => PackageCallbackCompletionState.Cancelled,
            _ => throw new SunderWorkerProtocolException(
                "Process worker callback completion state is unsupported."),
        };
        return new PackageCallbackCompletionResult(
            _package.PackageId,
            returnedSessionId,
            state,
            ReadBoundedString(value, "message", 2048));
    }

    private PackageAuthSessionStartResult? ReadAuthStartResult(JsonElement value, string sessionId)
    {
        var callback = ReadCallbackStartResult(value, sessionId);
        return callback is null
            ? null
            : new PackageAuthSessionStartResult(
                callback.PackageId,
                callback.CallbackSessionId,
                PackageAuthFlowKind.Browser,
                callback.LaunchUrl,
                callback.Message);
    }

    private PackageAuthStatus ReadAuthStatus(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "packageId",
            "status",
            "message",
            "canAuthorize",
            "canDisconnect");
        RequirePackageIdentity(value);
        var status = SunderWorkerProtocol.RequiredString(value, "status", 32) switch
        {
            "unavailable" => PackageAuthStatusKind.Unavailable,
            "not-connected" => PackageAuthStatusKind.NotConnected,
            "connected" => PackageAuthStatusKind.Connected,
            "failed" => PackageAuthStatusKind.Failed,
            _ => throw new SunderWorkerProtocolException("Process worker auth status is unsupported."),
        };
        return new PackageAuthStatus(
            _package.PackageId,
            status,
            ReadBoundedString(value, "message", 2048),
            ReadBoolean(value, "canAuthorize"),
            ReadBoolean(value, "canDisconnect"));
    }

    private void RequirePackageIdentity(JsonElement value)
    {
        if (!string.Equals(
                SunderWorkerProtocol.RequiredString(value, "packageId", 256),
                _package.PackageId,
                StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker contribution response changed the Host-owned package id.");
        }
    }

    private static bool ReadBoolean(JsonElement value, string propertyName)
        => SunderWorkerProtocol.RequiredValue(value, propertyName).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SunderWorkerProtocolException(
                $"Process worker contribution property '{propertyName}' must be a boolean."),
        };

    private static string ReadBoundedString(JsonElement value, string propertyName, int maximumLength)
    {
        var property = SunderWorkerProtocol.RequiredValue(value, propertyName);
        if (property.ValueKind != JsonValueKind.String
            || property.GetString()!.Length > maximumLength
            || property.GetString()!.Any(static character =>
                char.IsControl(character) && character != '\t'))
        {
            throw new SunderWorkerProtocolException(
                $"Process worker contribution property '{propertyName}' must be a bounded string.");
        }
        return property.GetString()!;
    }

    private T ValidateContributionResult<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (SunderWorkerProtocolException exception)
        {
            FaultProtocol(exception);
            throw;
        }
    }

    private sealed class ProcessWorkerCallbackHandler(
        ProcessRuntimeWorker worker,
        string callbackHandlerId) : IPackageCallbackHandler
    {
        public string CallbackHandlerId { get; } = callbackHandlerId;

        public async Task<PackageCallbackStartResult?> StartCallbackAsync(
            PackageCallbackStartContext context,
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.callback-start",
                "worker.callback-started",
                new
                {
                    handlerId = CallbackHandlerId,
                    callbackSessionId = context.CallbackSessionId,
                    callbackUri = context.CallbackUri.AbsoluteUri,
                    purpose = context.Purpose,
                    parameters = context.Parameters,
                },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() =>
                worker.ReadCallbackStartResult(value, context.CallbackSessionId));
        }

        public async Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
            PackageCallbackCompletionContext context,
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.callback-complete",
                "worker.callback-completed",
                new
                {
                    handlerId = CallbackHandlerId,
                    callbackSessionId = context.CallbackSessionId,
                    queryValues = context.QueryValues,
                },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() =>
                worker.ReadCallbackCompletionResult(value, context.CallbackSessionId));
        }

        public async Task CancelCallbackAsync(
            PackageCallbackCancellationContext context,
            CancellationToken cancellationToken = default)
            => _ = await worker.InvokeContributionAsync(
                "host.callback-cancel",
                "worker.callback-cancelled",
                new
                {
                    handlerId = CallbackHandlerId,
                    callbackSessionId = context.CallbackSessionId,
                    reason = CancellationReason(context.Reason),
                },
                cancellationToken).ConfigureAwait(false);
    }

    private sealed class ProcessWorkerAuthHandler(ProcessRuntimeWorker worker) : IPackageAuthHandler
    {
        public async ValueTask<PackageAuthStatus> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.auth-status",
                "worker.auth-status",
                new { },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() => worker.ReadAuthStatus(value));
        }

        public async Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(
            PackageAuthSessionStartContext context,
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.auth-connect",
                "worker.auth-connected",
                new
                {
                    authSessionId = context.AuthSessionId,
                    callbackUri = context.CallbackUri.AbsoluteUri,
                },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() =>
                worker.ReadAuthStartResult(value, context.AuthSessionId));
        }

        public async Task<PackageAuthStatus> CompleteAuthorizationAsync(
            PackageAuthSessionCompletionContext context,
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.auth-complete",
                "worker.auth-completed",
                new
                {
                    authSessionId = context.AuthSessionId,
                    queryValues = context.QueryValues,
                },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() => worker.ReadAuthStatus(value));
        }

        public async Task<PackageAuthStatus> DisconnectAsync(
            CancellationToken cancellationToken = default)
        {
            var value = await worker.InvokeContributionAsync(
                "host.auth-disconnect",
                "worker.auth-disconnected",
                new { },
                cancellationToken).ConfigureAwait(false);
            return worker.ValidateContributionResult(() => worker.ReadAuthStatus(value));
        }

        public async Task CancelCallbackAsync(
            PackageCallbackCancellationContext context,
            CancellationToken cancellationToken = default)
            => _ = await worker.InvokeContributionAsync(
                "host.callback-cancel",
                "worker.callback-cancelled",
                new
                {
                    handlerId = PackageCallbackHandlerIds.Authentication,
                    callbackSessionId = context.CallbackSessionId,
                    reason = CancellationReason(context.Reason),
                },
                cancellationToken).ConfigureAwait(false);
    }

    private static string CancellationReason(PackageCallbackCancellationReason reason)
        => reason switch
        {
            PackageCallbackCancellationReason.Expired => "expired",
            PackageCallbackCancellationReason.PackageUnloaded => "package-unloaded",
            PackageCallbackCancellationReason.HostShutdown => "host-shutdown",
            _ => "caller-requested",
        };

    private sealed class WorkerContributionCall(
        string id,
        string responseType,
        long sessionGeneration)
    {
        public string Id { get; } = id;
        public string ResponseType { get; } = responseType;
        public long SessionGeneration { get; } = sessionGeneration;
        public TaskCompletionSource<JsonElement> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; set; }
        public bool Terminal { get; set; }
    }

    private sealed record ContributionCancellation(
        ProcessRuntimeWorker Worker,
        WorkerContributionCall Call,
        CancellationToken Token);
}

internal sealed class ProcessWorkerContributionException(string code, string message)
    : ProcessRuntimeWorkerException($"Worker contribution '{code}' failed: {message}")
{
    public string Code { get; } = code;
}
