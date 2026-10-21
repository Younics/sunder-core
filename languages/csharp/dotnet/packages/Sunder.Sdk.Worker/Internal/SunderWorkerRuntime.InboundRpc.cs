using System.Text.Json;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
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
        var callerPackageVersion = WorkerJson.RequiredString(
            contextValue,
            "callerPackageVersion",
            SemanticVersion.MaximumLength);
        var deadlineUtc = WorkerJson.RequiredUtcTimestamp(contextValue, "deadlineUtc");
        var callDepthValue = WorkerJson.RequiredNonNegativeInt64(contextValue, "callDepth");
        if (callDepthValue is < 1 or > int.MaxValue)
        {
            throw new WorkerProtocolException("Host invocation callDepth is invalid.");
        }
        var provider = WorkerWire.ReadProviderSnapshot(
            WorkerJson.RequiredValue(contextValue, "provider"));

        long sessionGeneration;
        lock (Gate)
        {
            if ((_phase != RuntimePhase.Active && !IsLifecycleStoppingUnderGate) || _sessionGeneration is null)
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
            || !string.Equals(provider.DescriptorSha256, registration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(provider.AbiSha256, registration.AbiSha256, StringComparison.Ordinal))
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
            registration,
            cancellation,
            authority,
            context,
            serviceId,
            methodId);
        lock (Gate)
        {
            if (IsLifecycleStoppingUnderGate)
            {
                authority.Revoke();
                cancellation.Dispose();
                if (_invocations.ContainsKey(id) || _contributions.ContainsKey(id) || _rememberedHostIds.Contains(id))
                    throw new WorkerProtocolException($"Host reused duplicate invocation id '{id}'.");
                // Already-written Host requests can cross the generation-fault report.
                // Never dispatch them into retired package code. The Host cancels these
                // calls when it observes the report and completes them at retirement.
                RememberHostId(id);
                return;
            }
            if (_phase != RuntimePhase.Active)
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException("Host invocation arrived while the worker was stopping.");
            }
            if (_invocations.ContainsKey(id) || _contributions.ContainsKey(id) || _rememberedHostIds.Contains(id))
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException($"Host reused duplicate invocation id '{id}'.");
            }
            var laneLimit = kind == InboundInvocationKind.ServerStream
                ? _limits.MaximumInboundStreams
                : _limits.MaximumInboundCalls;
            if (_invocations.Values.Count(item => item.Kind == kind) >= laneLimit)
            {
                authority.Revoke();
                cancellation.Dispose();
                throw new WorkerProtocolException("Host exceeded the worker outstanding invocation limit.");
            }
            _invocations.Add(id, invocation);
        }

        _ = RunInvocationAsync(
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
        var previousInvocationId = _currentInvocationId.Value;
        _currentInvocationId.Value = invocation.Id;
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
            _currentInvocationId.Value = previousInvocationId;
            Task cancellation;
            lock (Gate)
            {
                invocation.CancellationSealed = true;
                cancellation = invocation.CancellationOperation ?? Task.CompletedTask;
            }
            await cancellation.ConfigureAwait(false);
            invocation.Authority.Revoke();
            invocation.Cancellation.Dispose();
            lock (Gate)
            {
                _invocations.Remove(invocation.Id);
                // A live invocation can outlast the bounded completion history.
                // Start its cancellation-crossing window only when it finishes.
                RememberHostId(invocation.Id);
            }
            invocation.Completion.TrySetResult();
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
        try
        {
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (WorkerTransportResourceException exception)
        {
            await WriteProviderFailureAsync(invocation, exception).ConfigureAwait(false);
        }
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
        JsonElement? details = null;
        if (exception is WorkerTransportResourceException)
        {
            kind = "resource-exhausted";
            code = "rpc.worker.response-limit";
            message = "The RPC response exceeds the negotiated worker transport limits.";
        }
        else if (invocation.HostCancelled)
        {
            kind = "cancelled";
            code = "rpc.call.cancelled";
            message = "The RPC call was cancelled.";
        }
        else if (_environment.OperationalOutcomesV1
                 && exception is SunderRpcException { IsHostAuthenticated: true } operational
                 && operational.Error.Kind is SunderRpcErrorKind.ResourceExhausted
                     or SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.DeadlineExceeded)
        {
            // This is a provider-reported operational outcome, not forwarded Host authority.
            // In particular, a nested NotDispatched/Safe rejection says nothing about whether
            // this enclosing handler has already performed a mutation. The Host owns that stamp.
            kind = "operational";
            code = operational.Error.Kind switch
            {
                SunderRpcErrorKind.ResourceExhausted => "resource-exhausted",
                SunderRpcErrorKind.Unavailable => "unavailable",
                _ => "deadline-exceeded",
            };
            message = "The provider could not complete the RPC operation.";
        }
        else if (exception is SunderRpcException rpcException
                 && rpcException.Error.Kind == SunderRpcErrorKind.Domain
                 && !rpcException.IsHostAuthenticated
                 && WorkerJson.IsSafeErrorCode(rpcException.Error.Code)
                 && invocation.Registration.IsDeclaredDomainError(
                     invocation.ServiceId,
                     invocation.MethodId,
                     rpcException.Error.Code))
        {
            kind = "domain";
            code = rpcException.Error.Code;
            message = WorkerDiagnosticSink.Sanitize(rpcException.Error.Message, 512);
            if (message.Length == 0) message = "The provider rejected the request.";
            details = rpcException.Error.Details;
        }
        else
        {
            kind = "provider-fault";
            code = "rpc.provider.handler-fault";
            message = "The process provider handler failed.";
            _diagnostics.Write("Process provider handler failed", exception);
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
            if (details is { } detail)
            {
                writer.WritePropertyName("details");
                detail.WriteTo(writer);
            }
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

    private enum InboundInvocationKind
    {
        Unary,
        ServerStream,
    }

    private sealed class InboundInvocation(
        string id,
        InboundInvocationKind kind,
        SunderWorkerProviderRegistration registration,
        CancellationTokenSource cancellation,
        WorkerInvocationAuthority authority,
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId)
    {
        public string Id { get; } = id;
        public InboundInvocationKind Kind { get; } = kind;
        public SunderWorkerProviderRegistration Registration { get; } = registration;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public WorkerInvocationAuthority Authority { get; } = authority;
        public SunderRpcInvocationContext Context { get; } = context;
        public string ServiceId { get; } = serviceId;
        public string MethodId { get; } = methodId;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Operation => Completion.Task;
        public Task? CancellationOperation { get; set; }
        public bool CancellationSealed { get; set; }
        public bool HostCancelled { get; set; }
    }
}
