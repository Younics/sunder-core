using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
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
                           OutboundStreamIdentity.Watch,
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
                WriteParentInvocationId(writer);
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
                WriteParentInvocationId(writer);
                writer.WriteEndObject();
            }),
            OutboundStreamIdentity.Subscribe(serviceId, methodId),
            cancellationToken);
    }
}
