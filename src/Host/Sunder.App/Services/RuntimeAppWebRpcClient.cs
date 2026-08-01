using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class RuntimeAppWebRpcClientFactory(
    Func<RuntimeConnectionInfo?> getRuntimeConnectionInfo) : IAppWebRpcClientFactory
{
    public async Task<IAppWebRpcClient> CreateAsync(
        ActivePackageWebStamp package,
        CancellationToken cancellationToken)
    {
        var management = getRuntimeConnectionInfo.Target is RuntimeClientTransport sharedTransport
            ? new RuntimeManagementClient(sharedTransport)
            : new RuntimeManagementClient(getRuntimeConnectionInfo);
        try
        {
            var session = await management.OpenAppRpcSessionAsync(
                new RuntimeRpcAppSessionOpenRequest(
                    package.PackageId,
                    package.PackageVersion,
                    package.ManifestSha256,
                    package.Target,
                    package.SessionGeneration,
                    package.AppGenerationId),
                cancellationToken).ConfigureAwait(false);
            return new RuntimeAppWebRpcClient(management, session.SessionId);
        }
        catch
        {
            management.Dispose();
            throw;
        }
    }
}

internal sealed class RuntimeAppWebRpcClient(
    RuntimeManagementClient management,
    string sessionId) : IAppWebRpcClient, ISunderRpcClient
{
    private int _disposed;

    public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = await management.GetAppRpcProviderAsync(
            new RuntimeRpcAppProviderRequest(sessionId, endpoint.Value),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        return response.Provider is null ? null : ToSdk(response.Provider);
    }

    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await management.DiscoverAppRpcAsync(
            new RuntimeRpcAppDiscoverRequest(sessionId, contractId),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        var snapshot = response.Snapshot
                       ?? throw Protocol("rpc.transport.empty-discovery", "The Runtime returned an empty RPC discovery response.");
        return ToSdk(snapshot);
    }

    ValueTask<SunderRpcCatalogSnapshot> ISunderRpcClient.DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken)
        => DiscoverAsync(contractId, cancellationToken);

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await foreach (var frame in management.WatchAppRpcAsync(
                           new RuntimeRpcAppWatchRequest(sessionId, afterRevision, afterSequence),
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (frame.Type)
            {
                case RuntimeRpcAppStreamFrameTypes.Event when frame.Event is not null:
                    yield return ToSdk(frame.Event);
                    break;
                case RuntimeRpcAppStreamFrameTypes.Completed:
                    yield break;
                case RuntimeRpcAppStreamFrameTypes.Error:
                    throw ToException(frame.Error);
                default:
                    throw Protocol("rpc.transport.invalid-frame", "The Runtime returned an invalid RPC catalog stream frame.");
            }
        }
        throw Protocol("rpc.transport.truncated-stream", "The Runtime RPC catalog stream ended without a terminal frame.");
    }

    IAsyncEnumerable<SunderRpcCatalogEvent> ISunderRpcClient.WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken)
        => WatchAsync(afterRevision, afterSequence, cancellationToken);

    public async ValueTask<JsonElement> InvokeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await management.InvokeAppRpcAsync(
            new RuntimeRpcAppInvokeRequest(
                sessionId,
                endpointReference,
                serviceId,
                methodId,
                request,
                deadlineUtc),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        return response.Value
               ?? throw Protocol("rpc.transport.empty-response", "The Runtime returned an empty RPC invocation response.");
    }

    ValueTask<JsonElement> ISunderRpcClient.InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => InvokeAsync(endpoint.Value, serviceId, methodId, request, options?.DeadlineUtc, cancellationToken);

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        JsonElement request,
        DateTimeOffset? deadlineUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await foreach (var frame in management.SubscribeAppRpcAsync(
                           new RuntimeRpcAppInvokeRequest(
                               sessionId,
                               endpointReference,
                               serviceId,
                               methodId,
                               request,
                               deadlineUtc),
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (frame.Type)
            {
                case RuntimeRpcAppStreamFrameTypes.Event when frame.Event is { } item:
                    yield return item;
                    break;
                case RuntimeRpcAppStreamFrameTypes.Completed:
                    yield break;
                case RuntimeRpcAppStreamFrameTypes.Error:
                    throw ToException(frame.Error);
                default:
                    throw Protocol("rpc.transport.invalid-frame", "The Runtime returned an invalid RPC subscription frame.");
            }
        }
        throw Protocol("rpc.transport.truncated-stream", "The Runtime RPC subscription ended without a terminal frame.");
    }

    IAsyncEnumerable<JsonElement> ISunderRpcClient.SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => SubscribeAsync(endpoint.Value, serviceId, methodId, request, options?.DeadlineUtc, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await management.CloseAppRpcSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to close a package App RPC caller session.", exception);
        }
        finally
        {
            management.Dispose();
        }
    }

    private static SunderRpcCatalogSnapshot ToSdk(RuntimeRpcCatalogSnapshot snapshot)
        => new(
            snapshot.Revision,
            snapshot.Sequence,
            snapshot.Providers.Select(ToSdk).ToArray(),
            snapshot.ResetRequired);

    private static SunderRpcProviderSnapshot ToSdk(RuntimeRpcProviderDescriptor provider)
        => new(
            provider.PackageId,
            provider.PackageVersion,
            provider.ProviderId,
            provider.ContractId,
            provider.ContractVersion,
            provider.ContractSha256,
            provider.ActivationId,
            provider.ActivationEpoch,
            provider.SessionGeneration,
            new SunderRpcEndpointReference(provider.EndpointReference),
            provider.CatalogRevision,
            provider.State switch
            {
                RuntimeRpcProviderState.Active => SunderRpcProviderState.Active,
                RuntimeRpcProviderState.Inactive => SunderRpcProviderState.Inactive,
                _ => SunderRpcProviderState.Faulted,
            },
            provider.FaultCode);

    private static SunderRpcCatalogEvent ToSdk(RuntimeRpcCatalogEventDescriptor value)
        => new(
            value.Revision,
            value.Sequence,
            value.Kind switch
            {
                RuntimeRpcCatalogEventKind.Added => SunderRpcCatalogEventKind.Added,
                RuntimeRpcCatalogEventKind.Removed => SunderRpcCatalogEventKind.Removed,
                RuntimeRpcCatalogEventKind.Activated => SunderRpcCatalogEventKind.Activated,
                RuntimeRpcCatalogEventKind.Deactivated => SunderRpcCatalogEventKind.Deactivated,
                RuntimeRpcCatalogEventKind.Faulted => SunderRpcCatalogEventKind.Faulted,
                _ => SunderRpcCatalogEventKind.ResetRequired,
            },
            value.Provider is null ? null : ToSdk(value.Provider));

    private static void ThrowIfError(RuntimeRpcErrorDescriptor? error)
    {
        if (error is not null)
        {
            throw ToException(error);
        }
    }

    private static SunderRpcException ToException(RuntimeRpcErrorDescriptor? error)
        => error is null
            ? Protocol("rpc.transport.empty-error", "The Runtime returned an empty RPC error frame.")
            : new SunderRpcException(new SunderRpcError(
                error.Kind switch
                {
                    RuntimeRpcErrorKind.Domain => SunderRpcErrorKind.Domain,
                    RuntimeRpcErrorKind.PermissionDenied => SunderRpcErrorKind.PermissionDenied,
                    RuntimeRpcErrorKind.NotFound => SunderRpcErrorKind.NotFound,
                    RuntimeRpcErrorKind.StaleEndpoint => SunderRpcErrorKind.StaleEndpoint,
                    RuntimeRpcErrorKind.Validation => SunderRpcErrorKind.Validation,
                    RuntimeRpcErrorKind.DeadlineExceeded => SunderRpcErrorKind.DeadlineExceeded,
                    RuntimeRpcErrorKind.Cancelled => SunderRpcErrorKind.Cancelled,
                    RuntimeRpcErrorKind.ResourceExhausted => SunderRpcErrorKind.ResourceExhausted,
                    RuntimeRpcErrorKind.Unavailable => SunderRpcErrorKind.Unavailable,
                    RuntimeRpcErrorKind.ProviderFaulted => SunderRpcErrorKind.ProviderFaulted,
                    _ => SunderRpcErrorKind.Protocol,
                },
                error.Code,
                error.Message));

    private static SunderRpcException Protocol(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Protocol, code, message));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
