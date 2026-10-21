using System.Net;
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
        var startupTrace = AppStartupTrace.BeginFirstUsefulRpc(package);
        RuntimeManagementClient? management = null;
        try
        {
            management = getRuntimeConnectionInfo.Target is RuntimeClientTransport sharedTransport
                ? new RuntimeManagementClient(sharedTransport)
                : new RuntimeManagementClient(getRuntimeConnectionInfo);
            var session = await management.OpenAppRpcSessionAsync(
                new RuntimeRpcAppSessionOpenRequest(
                    package.PackageId,
                    package.PackageVersion,
                    package.ManifestSha256,
                    package.Target,
                    package.SessionGeneration,
                    package.AppGenerationId) { LoadPermitId = package.LoadPermitId },
                cancellationToken).ConfigureAwait(false);
            return new RuntimeAppWebRpcClient(management, session.SessionId, startupTrace);
        }
        catch (Exception exception)
        {
            startupTrace?.Fail(exception);
            management?.Dispose();
            throw;
        }
    }
}

internal sealed partial class RuntimeAppWebRpcClient : IAppWebRpcClient, ISunderRpcClient
{
    private readonly RuntimeManagementClient management;
    private readonly string sessionId;
    private readonly RuntimeClientResourceLimits _resourceLimits;
    private readonly SunderRpcResourceProfile? _resourceProfile;
    private readonly AppStartupTraceOperation? _firstUsefulRpcTrace;
    private int _disposed;

    public RuntimeAppWebRpcClient(
        RuntimeManagementClient management,
        string sessionId,
        AppStartupTraceOperation? firstUsefulRpcTrace = null)
    {
        this.management = management;
        this.sessionId = sessionId;
        _firstUsefulRpcTrace = firstUsefulRpcTrace;
        _resourceLimits = management.ResourceLimits;
        _resourceProfile = ToSdk(_resourceLimits.Profile);
    }

    public RuntimeClientResourceLimits ResourceLimits => _resourceLimits;

    public SunderRpcResourceProfile? ResourceProfile => _resourceProfile;

    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await ReadRuntimeAsync(management.OpenAppRpcCallScopeAsync(
            new RuntimeRpcAppCallScopeOpenRequest(sessionId, options?.DeadlineUtc),
            cancellationToken)).ConfigureAwait(false);
        ThrowIfError(result.Error);
        var descriptor = result.Scope
                         ?? throw Protocol(
                             "rpc.transport.empty-call-scope",
                             "The Runtime returned an empty RPC call scope.");
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
        var response = await ReadRuntimeAsync(management.GetAppRpcProviderAsync(
            new RuntimeRpcAppProviderRequest(sessionId, endpoint.Value, callScopeId),
            cancellationToken)).ConfigureAwait(false);
        ThrowIfError(response.Error);
        if (response.Provider is null)
        {
            return null;
        }
        RecordFirstUsefulRpc("provider");
        return ToSdk(response.Provider);
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
        var response = await ReadRuntimeAsync(management.TryReportAppRpcInvariantViolationAsync(
            new RuntimeRpcAppInvariantViolationRequest(
                sessionId,
                endpoint.Value,
                SanitizeExceptionMessage(exception.Message),
                callScopeId),
            cancellationToken)).ConfigureAwait(false);
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
        var response = await ReadRuntimeAsync(management.DiscoverAppRpcAsync(
            new RuntimeRpcAppDiscoverRequest(sessionId, contractId, callScopeId),
            cancellationToken)).ConfigureAwait(false);
        ThrowIfError(response.Error);
        var snapshot = response.Snapshot
                       ?? throw Protocol("rpc.transport.empty-discovery", "The Runtime returned an empty RPC discovery response.");
        if (snapshot.Providers.Count > 0)
        {
            RecordFirstUsefulRpc("discover");
        }
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
        await foreach (var frame in ReadRuntimeStreamAsync(management.WatchAppRpcAsync(
            new RuntimeRpcAppWatchRequest(sessionId, afterRevision, afterSequence, callScopeId),
                           cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            switch (frame.Type)
            {
                case RuntimeRpcAppStreamFrameTypes.Event when frame.Event is not null:
                    RecordFirstUsefulRpc("watch");
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
        var (response, hasExplicitValidDispatchMetadata) = await ReadRuntimeAsync(management.InvokeAppRpcWithDispatchMetadataAsync(
            new RuntimeRpcAppInvokeRequest(
                sessionId,
                endpointReference,
                serviceId,
                methodId,
                request,
                deadlineUtc,
                callScopeId),
            cancellationToken)).ConfigureAwait(false);
        if (response.Value is not null && response.Error is not null)
            throw Protocol("rpc.transport.conflicting-invoke-frame", "The Runtime returned both an RPC result and an error.");
        ThrowIfError(response.Error, hostAuthenticated: hasExplicitValidDispatchMetadata);
        var value = response.Value
                    ?? throw Protocol("rpc.transport.empty-response", "The Runtime returned an empty RPC invocation response.");
        RecordFirstUsefulRpc("invoke");
        return value;
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
        await foreach (var frame in ReadRuntimeStreamAsync(management.SubscribeAppRpcAsync(
                           new RuntimeRpcAppInvokeRequest(
                               sessionId,
                               endpointReference,
                               serviceId,
                                methodId,
                                request,
                                deadlineUtc,
                                callScopeId),
                           cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            switch (frame.Type)
            {
                case RuntimeRpcAppStreamFrameTypes.Event when frame.Event is { } item:
                    RecordFirstUsefulRpc("subscribe");
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
        var response = await ReadRuntimeAsync(management.RegisterAppRpcContentAsync(
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
            cancellationToken)).ConfigureAwait(false);
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
        var result = await ReadRuntimeAsync(management.OpenAppRpcContentAsync(
            new RuntimeRpcAppContentOpenRequest(
                sessionId,
                callScopeId,
                ToProtocol(reference)),
            cancellationToken)).ConfigureAwait(false);
        ThrowIfError(result.Error);
        return result.Content
               ?? throw Protocol(
                   "rpc.transport.empty-content",
                   "The Runtime returned an empty RPC content stream.");
    }

    internal async ValueTask CloseCallScopeAsync(string callScopeId)
    {
        // Retiring the caller session also closes its scopes. A borrowed scope can
        // finish draining after that retirement without reopening the old client.
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            await management.CloseAppRpcCallScopeAsync(
                new RuntimeRpcAppCallScopeCloseRequest(sessionId, callScopeId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _firstUsefulRpcTrace?.Complete("unused", operation: "none");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await management.CloseAppRpcSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedRuntimeRestartFailure(exception))
        {
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

    private void RecordFirstUsefulRpc(string operation)
        => _firstUsefulRpcTrace?.Complete(operation: operation);

    private static SunderRpcCatalogSnapshot ToSdk(RuntimeRpcCatalogSnapshot snapshot)
        => new(
            snapshot.Revision,
            snapshot.Sequence,
            snapshot.Providers.Select(ToSdk).ToArray(),
            snapshot.ResetRequired);

    private static SunderRpcResourceProfile? ToSdk(RuntimeClientResourceProfile? profile)
    {
        if (profile is null) return null;
        var policy = profile.EffectivePolicy;
        return new SunderRpcResourceProfile(
            profile.EffectiveProfileHash,
            policy.MaximumRpcValueBytes,
            policy.MaximumValueDepth,
            policy.MaximumValueNodes,
            policy.MaximumObjectMembers,
            policy.MaximumInlineDecodedBytes,
            policy.MaximumContentReferenceBytes);
    }

    private static SunderRpcProviderSnapshot ToSdk(RuntimeRpcProviderDescriptor provider)
        => new(
            provider.PackageId,
            provider.PackageVersion,
            provider.ProviderId,
            provider.ContractId,
            provider.ContractVersion,
            provider.DescriptorSha256,
            provider.AbiSha256,
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

    private void ThrowIfError(RuntimeRpcErrorDescriptor? error, bool hostAuthenticated = false)
    {
        if (error is not null)
        {
            throw ToException(error, hostAuthenticated);
        }
    }

    private SunderRpcException ToException(RuntimeRpcErrorDescriptor? error, bool hostAuthenticated = false)
    {
        if (error is null) return Protocol("rpc.transport.empty-error", "The Runtime returned an empty RPC error frame.");
        if (!Enum.IsDefined(error.Kind) || !Enum.IsDefined(error.DispatchState) || !Enum.IsDefined(error.RetrySafety)
            || string.IsNullOrWhiteSpace(error.Code))
            return Protocol("rpc.transport.invalid-error", "The Runtime returned invalid RPC error metadata.");
        return new SunderRpcException(new SunderRpcError(
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
                error.Message,
                error.DispatchState == RuntimeRpcDispatchState.NotDispatched
                    ? SunderRpcDispatchState.NotDispatched
                    : SunderRpcDispatchState.PossiblyDispatched,
                error.RetrySafety == RuntimeRpcRetrySafety.Safe
                    ? SunderRpcRetrySafety.Safe
                    : SunderRpcRetrySafety.Unsafe,
                error.Details), hostAuthenticated);
    }

    private static SunderRpcException Protocol(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Protocol, code, message));

    private static async Task<T> ReadRuntimeAsync<T>(Task<T> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (RuntimeClientException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw RuntimeUnavailable(exception);
        }
    }

    private static async IAsyncEnumerable<T> ReadRuntimeStreamAsync<T>(IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try { hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (RuntimeClientException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw RuntimeUnavailable(exception);
            }
            if (!hasNext) yield break;
            yield return enumerator.Current;
        }
    }

    private static SunderRpcException RuntimeUnavailable(RuntimeClientException exception)
    {
        // Host publication/restart can reject HTTP before an RPC envelope exists.
        // Package-owned observers need the host-neutral availability kind to recover.
        // HTTP alone cannot prove dispatch or retry safety; never replay a command here.
        var result = new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Unavailable,
            exception.ErrorCode ?? "rpc.transport.unavailable", "The Runtime RPC transport is temporarily unavailable. Reconnect before retrying.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        if (!string.IsNullOrWhiteSpace(exception.CorrelationId)) result.Data["RuntimeCorrelationId"] = exception.CorrelationId;
        return result;
    }

    private static string SanitizeExceptionMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The provider violated an orchestrator invariant.";
        var sanitized = new string(message.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        if (sanitized.Length == 0) return "The provider violated an orchestrator invariant.";
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    internal static bool IsExpectedRuntimeRestartFailure(Exception exception)
        => exception is RuntimeClientException { StatusCode: HttpStatusCode.ServiceUnavailable };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class RuntimeAppWebRpcCallScope(
    RuntimeAppWebRpcClient client,
    RuntimeRpcAppCallScopeDescriptor descriptor) : ISunderRpcCallScope, ISunderRpcTrustedTransportScope
{
    private int _disposed;

    public DateTimeOffset DeadlineUtc => descriptor.DeadlineUtc;

    public SunderRpcResourceProfile? ResourceProfile => client.ResourceProfile;

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
        catch (Exception exception) when (RuntimeAppWebRpcClient.IsExpectedRuntimeRestartFailure(exception))
        {
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to close a package App RPC call scope.", exception);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
