using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
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
                    WriteParentInvocationId(writer);
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
            "worker.content-release",
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
}
