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

    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var descriptor = await management.OpenAppRpcCallScopeAsync(
            new RuntimeRpcAppCallScopeOpenRequest(sessionId, options?.DeadlineUtc),
            cancellationToken).ConfigureAwait(false);
        return new RuntimeAppWebRpcCallScope(this, descriptor);
    }

    public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => await GetProviderAsync(endpoint, callScopeId: null, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        string? callScopeId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await management.GetAppRpcProviderAsync(
            new RuntimeRpcAppProviderRequest(sessionId, endpoint.Value, callScopeId),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        return response.Provider is null ? null : ToSdk(response.Provider);
    }

    public async ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => await TryReportInvariantViolationAsync(
            endpoint,
            exception,
            callScopeId: null,
            cancellationToken).ConfigureAwait(false);

    internal async ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        string? callScopeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(exception);
        ThrowIfDisposed();
        var response = await management.TryReportAppRpcInvariantViolationAsync(
            new RuntimeRpcAppInvariantViolationRequest(
                sessionId,
                endpoint.Value,
                SanitizeExceptionMessage(exception.Message),
                callScopeId),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        return response.Accepted;
    }

    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken)
        => await DiscoverAsync(contractId, callScopeId: null, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        string? callScopeId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await management.DiscoverAppRpcAsync(
            new RuntimeRpcAppDiscoverRequest(sessionId, contractId, callScopeId),
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
        await foreach (var item in WatchAsync(
                           afterRevision,
                           afterSequence,
                           callScopeId: null,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        string? callScopeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await foreach (var frame in management.WatchAppRpcAsync(
            new RuntimeRpcAppWatchRequest(sessionId, afterRevision, afterSequence, callScopeId),
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
        => await InvokeAsync(
            endpointReference,
            serviceId,
            methodId,
            request,
            deadlineUtc,
            callScopeId: null,
            cancellationToken).ConfigureAwait(false);

    internal async ValueTask<JsonElement> InvokeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        JsonElement request,
        DateTimeOffset? deadlineUtc,
        string? callScopeId,
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
                deadlineUtc,
                callScopeId),
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
        await foreach (var item in SubscribeAsync(
                           endpointReference,
                           serviceId,
                           methodId,
                           request,
                           deadlineUtc,
                           callScopeId: null,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    internal async IAsyncEnumerable<JsonElement> SubscribeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        JsonElement request,
        DateTimeOffset? deadlineUtc,
        string? callScopeId,
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
                                deadlineUtc,
                                callScopeId),
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

    internal async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        string callScopeId,
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await management.RegisterAppRpcContentAsync(
            new RuntimeRpcAppContentRegisterMetadata(
                sessionId,
                callScopeId,
                endpoint.Value,
                options.MediaType,
                options.FileName,
                options.Length,
                options.ExpiresAtUtc,
                options.Repeatability == SunderRpcContentRepeatability.SingleUse
                    ? RuntimeRpcContentRepeatability.SingleUse
                    : RuntimeRpcContentRepeatability.Repeatable,
                options.MaximumUses),
            source,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.Error);
        return response.Content is null
            ? throw Protocol(
                "rpc.transport.empty-content-reference",
                "The Runtime returned an empty RPC content registration response.")
            : ToSdk(response.Content);
    }

    internal async ValueTask<Stream> OpenContentAsync(
        string callScopeId,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var result = await management.OpenAppRpcContentAsync(
            new RuntimeRpcAppContentOpenRequest(
                sessionId,
                callScopeId,
                ToProtocol(reference)),
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(result.Error);
        return result.Content
               ?? throw Protocol(
                   "rpc.transport.empty-content",
                   "The Runtime returned an empty RPC content stream.");
    }

    internal async ValueTask CloseCallScopeAsync(string callScopeId)
    {
        ThrowIfDisposed();
        await management.CloseAppRpcCallScopeAsync(
            new RuntimeRpcAppCallScopeCloseRequest(sessionId, callScopeId),
            CancellationToken.None).ConfigureAwait(false);
    }

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

    private static SunderRpcContentReference ToSdk(RuntimeRpcContentReferenceDescriptor reference)
        => new(
            reference.Id,
            reference.Length,
            reference.Sha256,
            reference.MediaType,
            reference.FileName,
            reference.ExpiresAtUtc,
            reference.Repeatability == RuntimeRpcContentRepeatability.SingleUse
                ? SunderRpcContentRepeatability.SingleUse
                : SunderRpcContentRepeatability.Repeatable);

    private static RuntimeRpcContentReferenceDescriptor ToProtocol(SunderRpcContentReference reference)
        => new(
            reference.Id,
            reference.Length,
            reference.Sha256,
            reference.MediaType,
            reference.FileName,
            reference.ExpiresAtUtc,
            reference.Repeatability == SunderRpcContentRepeatability.SingleUse
                ? RuntimeRpcContentRepeatability.SingleUse
                : RuntimeRpcContentRepeatability.Repeatable);

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

    private static string SanitizeExceptionMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The provider violated an orchestrator invariant.";
        var sanitized = new string(message.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        if (sanitized.Length == 0) return "The provider violated an orchestrator invariant.";
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class RuntimeAppWebRpcCallScope(
    RuntimeAppWebRpcClient client,
    RuntimeRpcAppCallScopeDescriptor descriptor) : ISunderRpcCallScope
{
    private int _disposed;

    public DateTimeOffset DeadlineUtc => descriptor.DeadlineUtc;

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.GetProviderAsync(endpoint, descriptor.CallScopeId, cancellationToken);
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.TryReportInvariantViolationAsync(
            endpoint,
            exception,
            descriptor.CallScopeId,
            cancellationToken);
    }

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.DiscoverAsync(contractId, descriptor.CallScopeId, cancellationToken);
    }

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.WatchAsync(
            afterRevision,
            afterSequence,
            descriptor.CallScopeId,
            cancellationToken);
    }

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.InvokeAsync(
            endpoint.Value,
            serviceId,
            methodId,
            request,
            options?.DeadlineUtc,
            descriptor.CallScopeId,
            cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.SubscribeAsync(
            endpoint.Value,
            serviceId,
            methodId,
            request,
            options?.DeadlineUtc,
            descriptor.CallScopeId,
            cancellationToken);
    }

    public ValueTask<SunderRpcContentReference> RegisterContentAsync(
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.RegisterContentAsync(
            descriptor.CallScopeId,
            endpoint,
            source,
            options,
            cancellationToken);
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var source = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await RegisterContentAsync(endpoint, source, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return client.OpenContentAsync(descriptor.CallScopeId, reference, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await client.CloseCallScopeAsync(descriptor.CallScopeId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to close a package App RPC call scope.", exception);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
