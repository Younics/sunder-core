using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppWebBridge : IAsyncDisposable
{
    internal const string Protocol = "sunder.bridge.v2";
    internal const int ProtocolVersion = 2;
    private const int MaxRememberedRequestIds = 2048;
    private readonly IAppWebView _webView;
    private readonly AppWebContentServer _contentServer;
    private readonly IAppWebRpcClient _rpc;
    private readonly AppWebRpcContractCatalog _contracts;
    private readonly ExternalBrowserService _externalBrowser;
    private readonly Action<Uri> _navigate;
    private readonly RuntimeClientResourceLimits _limits;
    private readonly JsonSerializerOptions _boundedJsonOptions;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedOrder = new();
    private readonly Dictionary<string, SunderRpcProviderSnapshot> _providerHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _handlesByEndpoint = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private string _nonce = CreateNonce();
    private readonly string _origin;
    private Uri? _currentUri;
    private long _documentGeneration;
    private bool _trustedNavigation;
    private int _disposed;

    public AppWebBridge(
        IAppWebView webView,
        AppWebContentServer contentServer,
        IAppWebRpcClient rpc,
        AppWebRpcContractCatalog contracts,
        ExternalBrowserService externalBrowser,
        Action<Uri> navigate,
        RuntimeClientResourceLimits? resourceLimits = null)
    {
        _webView = webView;
        _contentServer = contentServer;
        _rpc = rpc;
        _contracts = contracts;
        _externalBrowser = externalBrowser;
        _navigate = navigate;
        _limits = resourceLimits ?? rpc.ResourceLimits;
        _boundedJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = _limits.MaximumJsonEnvelopeDepth,
        };
        _origin = contentServer.Origin.GetLeftPart(UriPartial.Authority);
    }

    internal string SessionNonce
    {
        get
        {
            lock (_gate) return _nonce;
        }
    }

    internal int PendingRequestCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    public void NavigationStarting()
    {
        PendingRequest[] requests;
        lock (_gate)
        {
            _trustedNavigation = false;
            _currentUri = null;
            _documentGeneration = checked(_documentGeneration + 1);
            _nonce = CreateNonce();
            requests = _pending.Values.ToArray();
            _pending.Clear();
            _rememberedIds.Clear();
            _rememberedOrder.Clear();
            _providerHandles.Clear();
            _handlesByEndpoint.Clear();
        }
        foreach (var request in requests) request.Cancel();
    }

    public async Task EstablishAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!AppWebNavigationPolicy.IsPackageUri(_contentServer, uri))
        {
            return;
        }
        long documentGeneration;
        string script;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _currentUri = uri;
            _trustedNavigation = true;
            documentGeneration = _documentGeneration;
            script = CreateBootstrapScript(_nonce);
        }
        try
        {
            await _webView.InvokeScriptAsync(script).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_documentGeneration == documentGeneration)
                {
                    _trustedNavigation = false;
                    _currentUri = null;
                }
            }
            throw;
        }
    }

    public async Task HandleMessageAsync(string? body, CancellationToken cancellationToken = default)
    {
        var recoveredRequestId = body is null ? null : TryRecoverRequestId(body);
        if (body is null)
        {
            RejectProtocol("The browser bridge message is empty.");
            return;
        }
        if (Encoding.UTF8.GetByteCount(body) > _limits.MaximumJsonEnvelopeBytes)
        {
            await RejectRecoveredAsync(
                recoveredRequestId,
                ResourceLimit(
                    "rpc.bridge.request-envelope-limit",
                    "The browser bridge request exceeds its negotiated envelope size limit."))
                .ConfigureAwait(false);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = _limits.MaximumJsonEnvelopeDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException)
        {
            await RejectRecoveredAsync(
                    recoveredRequestId,
                    ResourceLimit(
                        "rpc.bridge.request-depth-or-json",
                        "The browser bridge request is invalid or exceeds its negotiated depth limit."))
                .ConfigureAwait(false);
            return;
        }
        using (document)
        {
            recoveredRequestId = TryReadSafeRequestId(document.RootElement, out var safeRequestId)
                ? safeRequestId
                : null;
            if (!TryReadRequest(document.RootElement, out var request, out var error))
            {
                await RejectRecoveredAsync(
                        recoveredRequestId,
                        new SunderRpcError(
                            SunderRpcErrorKind.Protocol,
                            "rpc.bridge.request-envelope",
                            "The browser bridge request envelope is invalid."),
                        error)
                    .ConfigureAwait(false);
                return;
            }

            if (request.Method == "bridge.cancel")
            {
                try
                {
                    CancelRequest(request);
                }
                catch (SunderRpcException exception)
                {
                    await RejectRecoveredAsync(request.RequestId, Sanitize(exception.Error)).ConfigureAwait(false);
                }
                return;
            }

            PendingRequest? pending = null;
            SunderRpcError? immediateError = null;
            string? protocolRejection = null;
            lock (_gate)
            {
                if (!_trustedNavigation
                    || _currentUri is null
                    || !string.Equals(request.Nonce, _nonce, StringComparison.Ordinal)
                    || !string.Equals(request.Origin, _origin, StringComparison.Ordinal))
                {
                    protocolRejection = "The browser bridge origin or nonce is invalid.";
                    immediateError = new SunderRpcError(
                        SunderRpcErrorKind.Protocol,
                        "rpc.bridge.authentication",
                        "The browser bridge origin or nonce is invalid.");
                }
                else if (!_rememberedIds.Add(request.RequestId))
                {
                    protocolRejection = $"The browser bridge reused request id '{request.RequestId}'.";
                    immediateError = new SunderRpcError(
                        SunderRpcErrorKind.Protocol,
                        "rpc.bridge.request-id-reused",
                        "The browser bridge request id was already used.");
                }
                else
                {
                    _rememberedOrder.Enqueue(request.RequestId);
                    while (_rememberedOrder.Count > MaxRememberedRequestIds)
                    {
                        _rememberedIds.Remove(_rememberedOrder.Dequeue());
                    }
                    if (_pending.Count >= RuntimeClientResourceLimits.MaximumPendingBrowserRequests)
                    {
                        immediateError = new SunderRpcError(
                            SunderRpcErrorKind.ResourceExhausted,
                            "rpc.bridge.pending-limit",
                            "The browser bridge request limit was reached.");
                    }
                    else
                    {
                        pending = PendingRequest.Create(
                            _documentGeneration,
                            cancellationToken,
                            _lifetime.Token,
                            RuntimeClientResourceLimits.BrowserRequestTimeout,
                            request.DeadlineUtc);
                        _pending.Add(request.RequestId, pending);
                    }
                }
            }

            if (immediateError is not null)
            {
                await RejectRecoveredAsync(request.RequestId, immediateError, protocolRejection)
                    .ConfigureAwait(false);
                return;
            }
            await ExecuteAsync(request, pending!).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        PendingRequest[] requests;
        lock (_gate)
        {
            _trustedNavigation = false;
            _currentUri = null;
            requests = _pending.Values.ToArray();
            _pending.Clear();
            _providerHandles.Clear();
            _handlesByEndpoint.Clear();
        }
        foreach (var request in requests)
        {
            request.Cancel();
            request.Dispose();
        }
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task ExecuteAsync(BridgeRequest request, PendingRequest pending)
    {
        try
        {
            pending.Token.ThrowIfCancellationRequested();
            switch (request.Method)
            {
                case "rpc.discover":
                {
                    var payload = RequireObject(request.Payload, "RPC discover payload", "contractId");
                    var contractId = RequiredText(payload, "contractId", 256);
                    var snapshot = await _rpc.DiscoverAsync(contractId, pending.Token).ConfigureAwait(false);
                    await FinishResultAsync(request.RequestId, pending, MapCatalog(snapshot)).ConfigureAwait(false);
                    return;
                }
                case "rpc.watch":
                {
                    var payload = RequireObject(
                        request.Payload,
                        "RPC watch payload",
                        "afterRevision",
                        "afterSequence");
                    var afterRevision = RequiredNonNegativeInteger(payload, "afterRevision");
                    var afterSequence = RequiredNonNegativeInteger(payload, "afterSequence");
                    await foreach (var item in _rpc.WatchAsync(
                                       afterRevision,
                                       afterSequence,
                                       pending.Token).WithCancellation(pending.Token).ConfigureAwait(false))
                    {
                        await SendEventAsync(request.RequestId, pending, MapCatalogEvent(item)).ConfigureAwait(false);
                    }
                    await FinishCompleteAsync(request.RequestId, pending).ConfigureAwait(false);
                    return;
                }
                case "rpc.invoke":
                {
                    var invocation = ReadInvocation(request.Payload);
                    var provider = ResolveProvider(invocation.ProviderHandle);
                    var method = _contracts.GetMethod(
                        provider,
                        invocation.ServiceId,
                        invocation.MethodId,
                        SunderRpcMethodKind.Unary);
                    _contracts.ValidateRequest(provider, method, invocation.Request);
                    pending.MarkPossiblyDispatched(
                        method.Idempotency == SunderRpcMethodIdempotency.Idempotent
                            ? SunderRpcRetrySafety.Safe
                            : SunderRpcRetrySafety.Unsafe);
                    var response = await _rpc.InvokeAsync(
                        provider.Endpoint.Value,
                        invocation.ServiceId,
                        invocation.MethodId,
                        invocation.Request,
                        request.DeadlineUtc,
                        pending.Token).ConfigureAwait(false);
                    try
                    {
                        EnsureBounded(response);
                        _contracts.ValidateOutput(provider, method, response);
                    }
                    catch (SunderRpcException exception)
                    {
                        throw WithDispatchMetadata(exception, pending);
                    }
                    await FinishResultAsync(request.RequestId, pending, response).ConfigureAwait(false);
                    return;
                }
                case "rpc.subscribe":
                {
                    var invocation = ReadInvocation(request.Payload);
                    var provider = ResolveProvider(invocation.ProviderHandle);
                    var method = _contracts.GetMethod(
                        provider,
                        invocation.ServiceId,
                        invocation.MethodId,
                        SunderRpcMethodKind.ServerStream);
                    _contracts.ValidateRequest(provider, method, invocation.Request);
                    pending.MarkPossiblyDispatched(SunderRpcRetrySafety.Unsafe);
                    await foreach (var item in _rpc.SubscribeAsync(
                                       provider.Endpoint.Value,
                                       invocation.ServiceId,
                                       invocation.MethodId,
                                       invocation.Request,
                                       request.DeadlineUtc,
                                       pending.Token).WithCancellation(pending.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            EnsureBounded(item);
                            _contracts.ValidateOutput(provider, method, item);
                        }
                        catch (SunderRpcException exception)
                        {
                            throw WithDispatchMetadata(exception, pending);
                        }
                        await SendEventAsync(request.RequestId, pending, item).ConfigureAwait(false);
                    }
                    await FinishCompleteAsync(request.RequestId, pending).ConfigureAwait(false);
                    return;
                }
                case "host.openExternal":
                {
                    var payload = RequireObject(request.Payload, "external link payload", "url");
                    var text = RequiredText(payload, "url", 2048);
                    if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                        || !AppWebNavigationPolicy.IsExternalUri(uri))
                    {
                        throw Validation("bridge.external.invalid", "The external link is invalid.");
                    }
                    pending.Token.ThrowIfCancellationRequested();
                    _externalBrowser.Open(uri);
                    await FinishResultAsync(request.RequestId, pending, new { opened = true }).ConfigureAwait(false);
                    return;
                }
                case "navigation.getState":
                {
                    RequireObject(request.Payload, "navigation state payload");
                    Uri? current;
                    lock (_gate) current = _currentUri;
                    var route = current is null
                        ? "/"
                        : "/" + current.AbsolutePath[_contentServer.BasePath.Length..];
                    await FinishResultAsync(request.RequestId, pending, new { route }).ConfigureAwait(false);
                    return;
                }
                case "navigation.navigate":
                {
                    var payload = RequireObject(request.Payload, "navigation payload", "route");
                    var route = RequiredText(payload, "route", SunderPackageFormat.MaxWebViewRouteLength);
                    if (!SunderPackageFormat.IsWebViewRoute(route))
                    {
                        throw Validation("bridge.navigation.invalid", "The package route is invalid.");
                    }
                    pending.Token.ThrowIfCancellationRequested();
                    _navigate(_contentServer.GetRouteUri(route));
                    await FinishResultAsync(request.RequestId, pending, new { accepted = true }).ConfigureAwait(false);
                    return;
                }
                default:
                    throw Validation("bridge.method.unknown", "The browser bridge method is not supported.");
            }
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
            var error = pending.TimedOut
                ? new SunderRpcError(
                    SunderRpcErrorKind.DeadlineExceeded,
                    "rpc.call.deadline-exceeded",
                    "The browser bridge request deadline elapsed.",
                    pending.DispatchState,
                    pending.RetrySafety)
                : new SunderRpcError(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.bridge.cancelled",
                    "The browser bridge request was cancelled.",
                    pending.DispatchState,
                    pending.RetrySafety);
            await FinishErrorAsync(request.RequestId, pending, error).ConfigureAwait(false);
        }
        catch (SunderRpcException exception)
        {
            await FinishErrorAsync(request.RequestId, pending, Sanitize(exception.Error)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("A package browser bridge request failed.", exception);
            await FinishErrorAsync(
                request.RequestId,
                pending,
                new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.bridge.unavailable",
                    "The browser bridge request could not be completed.",
                    pending.DispatchState,
                    pending.RetrySafety)).ConfigureAwait(false);
        }
    }

    private void CancelRequest(BridgeRequest request)
    {
        var payload = RequireObject(request.Payload, "bridge cancellation payload", "targetRequestId");
        var target = RequiredId(payload, "targetRequestId");
        lock (_gate)
        {
            if (!_trustedNavigation
                || _currentUri is null
                || !string.Equals(request.Nonce, _nonce, StringComparison.Ordinal)
                || !string.Equals(request.Origin, _origin, StringComparison.Ordinal))
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Protocol,
                    "rpc.bridge.cancel-authentication",
                    "The browser bridge cancellation has an invalid origin or nonce."));
            }
            if (!_pending.TryGetValue(target, out var pending))
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Protocol,
                    "rpc.bridge.cancel-target",
                    "The browser bridge cancellation target is not pending."));
            }
            pending.Cancel();
        }
    }

    private BridgeInvocation ReadInvocation(JsonElement payloadValue)
    {
        var payload = RequireObject(
            payloadValue,
            "RPC invocation payload",
            "providerHandle",
            "serviceId",
            "methodId",
            "request");
        var request = payload.GetProperty("request").Clone();
        EnsureBounded(request);
        return new BridgeInvocation(
            RequiredId(payload, "providerHandle"),
            RequiredText(payload, "serviceId", 128),
            RequiredText(payload, "methodId", 128),
            request);
    }

    private SunderRpcProviderSnapshot ResolveProvider(string handle)
    {
        lock (_gate)
        {
            return _providerHandles.TryGetValue(handle, out var provider)
                ? provider
                : throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.StaleEndpoint,
                    "rpc.bridge.provider-handle",
                    "The provider handle is unknown or retired."));
        }
    }

    private object MapCatalog(SunderRpcCatalogSnapshot snapshot)
        => new
        {
            revision = snapshot.Revision,
            sequence = snapshot.Sequence,
            providers = snapshot.Providers.Select(MapProvider).ToArray(),
            resetRequired = snapshot.ResetRequired,
        };

    private object MapCatalogEvent(SunderRpcCatalogEvent value)
        => new
        {
            revision = value.Revision,
            sequence = value.Sequence,
            kind = value.Kind switch
            {
                SunderRpcCatalogEventKind.Added => "added",
                SunderRpcCatalogEventKind.Removed => "removed",
                SunderRpcCatalogEventKind.Activated => "activated",
                SunderRpcCatalogEventKind.Deactivated => "deactivated",
                SunderRpcCatalogEventKind.Faulted => "faulted",
                _ => "reset-required",
            },
            provider = value.Provider is null ? null : MapProvider(value.Provider),
        };

    private object MapProvider(SunderRpcProviderSnapshot provider)
    {
        string handle;
        lock (_gate)
        {
            if (!_handlesByEndpoint.TryGetValue(provider.Endpoint.Value, out handle!))
            {
                handle = "p_" + Base64Url(RandomNumberGenerator.GetBytes(24));
                _handlesByEndpoint.Add(provider.Endpoint.Value, handle);
            }
            _providerHandles[handle] = provider;
            if (provider.State != SunderRpcProviderState.Active)
            {
                _providerHandles.Remove(handle);
            }
        }
        return new
        {
            packageId = provider.PackageId,
            packageVersion = provider.PackageVersion,
            providerId = provider.ProviderId,
            contractId = provider.ContractId,
            contractVersion = provider.ContractVersion,
            descriptorSha256 = provider.DescriptorSha256,
            abiSha256 = provider.AbiSha256
                ?? throw new InvalidDataException("The Runtime provider is missing its V2 ABI identity."),
            providerHandle = handle,
            catalogRevision = provider.CatalogRevision,
            state = provider.State switch
            {
                SunderRpcProviderState.Active => "active",
                SunderRpcProviderState.Inactive => "inactive",
                _ => "faulted",
            },
            faultCode = provider.FaultCode,
        };
    }

    private Task FinishResultAsync(string requestId, PendingRequest pending, object? value)
        => FinishAsync(requestId, pending, "result", value, error: null);

    private Task FinishCompleteAsync(string requestId, PendingRequest pending)
        => FinishAsync(requestId, pending, "complete", value: null, error: null);

    private Task FinishErrorAsync(string requestId, PendingRequest pending, SunderRpcError error)
        => FinishAsync(requestId, pending, "error", value: null, error);

    private async Task FinishAsync(
        string requestId,
        PendingRequest pending,
        string type,
        object? value,
        SunderRpcError? error)
    {
        var send = false;
        lock (_gate)
        {
            if (_pending.TryGetValue(requestId, out var current)
                && ReferenceEquals(current, pending))
            {
                _pending.Remove(requestId);
                send = pending.TryComplete();
            }
            else
            {
                pending.TryComplete();
            }
        }
        try
        {
            if (send)
            {
                var sent = await TrySendAsync(requestId, pending, type, value, error).ConfigureAwait(false);
                if (!sent && error is null)
                {
                    await TrySendAsync(
                        requestId,
                        pending,
                        "error",
                        value: null,
                        ResponseLimit(pending)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    private async Task SendEventAsync(string requestId, PendingRequest pending, object? value)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var current)
                || !ReferenceEquals(current, pending)
                || pending.IsCompleted
                || !_trustedNavigation
                || pending.DocumentGeneration != _documentGeneration)
            {
                return;
            }
        }
        if (!await TrySendAsync(requestId, pending, "event", value, error: null).ConfigureAwait(false))
        {
            throw new SunderRpcException(ResponseLimit(pending));
        }
    }

    private async Task<bool> TrySendAsync(
        string requestId,
        PendingRequest pending,
        string type,
        object? value,
        SunderRpcError? error)
    {
        string nonce;
        lock (_gate)
        {
            if (!_trustedNavigation
                || _currentUri is null
                || pending.DocumentGeneration != _documentGeneration
                || Volatile.Read(ref _disposed) != 0)
            {
                return true;
            }
            nonce = _nonce;
        }
        var envelope = new
        {
            protocol = Protocol,
            version = ProtocolVersion,
            nonce,
            requestId,
            type,
            value,
            error = error is null ? null : MapError(error),
        };
        if (error?.Details is { } details && !IsBounded(details)) return false;
        byte[] utf8;
        try
        {
            utf8 = JsonSerializer.SerializeToUtf8Bytes(envelope, _boundedJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        if (utf8.Length > _limits.MaximumJsonEnvelopeBytes)
        {
            return false;
        }
        if (error is null && type is "result" or "event")
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                MaxDepth = _limits.MaximumJsonEnvelopeDepth,
            });
            var emittedValue = document.RootElement.GetProperty("value");
            if (Encoding.UTF8.GetByteCount(emittedValue.GetRawText())
                    > _limits.EffectivePolicy.MaximumRpcValueBytes
                || Depth(emittedValue) > _limits.EffectivePolicy.MaximumValueDepth)
            {
                return false;
            }
        }
        var json = Encoding.UTF8.GetString(utf8);
        var script = $"window.__sunderReceive?.({JsonSerializer.Serialize(json)});";
        await _webView.InvokeScriptAsync(script).ConfigureAwait(false);
        return true;
    }

    private string CreateBootstrapScript(string sessionNonce)
    {
        var protocol = JsonSerializer.Serialize(Protocol);
        var nonce = JsonSerializer.Serialize(sessionNonce);
        var origin = JsonSerializer.Serialize(_origin);
        var profileHash = JsonSerializer.Serialize(_limits.Profile?.EffectiveProfileHash);
        var runtimeInstanceId = JsonSerializer.Serialize(_limits.Profile?.RuntimeInstanceId.ToString("D"));
        var reportedByRuntime = _limits.IsRuntimeReported ? "true" : "false";
        var policy = _limits.EffectivePolicy;
        return FormattableString.Invariant($$$"""
            (() => {
              'use strict';
              const protocol = {{{protocol}}};
              const version = 2;
              const nonce = {{{nonce}}};
              const expectedOrigin = {{{origin}}};
              const encoder = new TextEncoder();
              const maxEnvelopeBytes = {{{_limits.MaximumJsonEnvelopeBytes}}};
              const maxPendingRequests = {{{RuntimeClientResourceLimits.MaximumPendingBrowserRequests}}};
              const maxRequestMilliseconds = {{{(long)RuntimeClientResourceLimits.BrowserRequestTimeout.TotalMilliseconds}}};
              const maxQueuedEvents = {{{RuntimeClientResourceLimits.MaximumQueuedBrowserEvents}}};
              const maxQueuedEventBytes = {{{_limits.MaximumQueuedBrowserEventBytes}}};
              const resourceProfile = Object.freeze({
                reportedByRuntime: {{{reportedByRuntime}}},
                runtimeInstanceId: {{{runtimeInstanceId}}},
                effectiveProfileHash: {{{profileHash}}},
                effectivePolicy: Object.freeze({
                  rpcValueBytes: {{{policy.MaximumRpcValueBytes}}},
                  valueDepth: {{{policy.MaximumValueDepth}}},
                  valueNodes: {{{policy.MaximumValueNodes}}},
                  objectMembers: {{{policy.MaximumObjectMembers}}},
                  inlineDecodedBytes: {{{policy.MaximumInlineDecodedBytes}}},
                  contentReferenceBytes: {{{policy.MaximumContentReferenceBytes}}}
                })
              });
              if (location.origin !== expectedOrigin || typeof window.invokeCSharpAction !== 'function') return false;
              const marker = `__sunderBridge_${nonce}`;
              if (window[marker] === true && window.sunder?.version === version && typeof window.__sunderReceive === 'function') return true;
              if (Object.prototype.hasOwnProperty.call(window, 'sunder')) return false;
              let sequence = 0;
              const pending = new Map();
              let queuedEventBytes = 0;
              const freezeJson = (value) => {
                if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
                  for (const child of Object.values(value)) freezeJson(child);
                  Object.freeze(value);
                }
                return value;
              };
              const failure = (kind, code, message, dispatchState = 'not-dispatched', retrySafety = 'safe', details) => {
                const error = { kind, code, message, dispatchState, retrySafety };
                if (details !== undefined) error.details = freezeJson(details);
                return Object.assign(new Error(message), {
                  name: 'SunderRpcError',
                  error: Object.freeze(error)
                });
              };
              const depth = (root) => {
                let maximum = 1;
                const stack = [{ value: root, depth: 1 }];
                while (stack.length) {
                  const current = stack.pop();
                  maximum = Math.max(maximum, current.depth);
                  if (current.value !== null && typeof current.value === 'object') {
                    for (const value of Object.values(current.value)) stack.push({ value, depth: current.depth + 1 });
                  }
                }
                return maximum;
              };
              const rejected = (error, stream) => {
                if (!stream) return Promise.reject(error);
                return Object.freeze({
                  [Symbol.asyncIterator]() {
                    let returned = false;
                    return {
                      next() {
                        if (returned) return Promise.resolve({ done: true, value: undefined });
                        returned = true;
                        return Promise.reject(error);
                      },
                      return() { returned = true; return Promise.resolve({ done: true, value: undefined }); }
                    };
                  }
                });
              };
              const finish = (state, error) => {
                if (state.done) return;
                state.done = true;
                state.error = error;
                if (state.timer !== null) clearTimeout(state.timer);
                state.signal?.removeEventListener('abort', state.cancel);
                pending.delete(state.requestId);
                for (const item of state.queue.splice(0)) queuedEventBytes -= item.size;
                for (const waiter of state.waiters.splice(0)) error ? waiter.fail(error) : waiter.ok({ done: true, value: undefined });
                if (!state.stream && error) state.reject(error);
              };
              const send = (method, payload, options, stream) => {
                const requestId = `b_${++sequence}`;
                let body;
                let normalized;
                try {
                  body = JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method, deadlineUtc: options?.deadline?.toISOString?.() ?? null, payload });
                  if (typeof body !== 'string') throw new TypeError('Request serialization returned no JSON.');
                  normalized = JSON.parse(body);
                } catch {
                  return rejected(failure('validation', 'rpc.bridge.serialization', 'The browser bridge request is not valid JSON.'), stream);
                }
                const value = method === 'rpc.invoke' || method === 'rpc.subscribe'
                  ? normalized.payload?.request
                  : normalized.payload;
                let valueJson;
                try { valueJson = JSON.stringify(value); } catch { valueJson = undefined; }
                if (typeof valueJson !== 'string'
                    || encoder.encode(valueJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                    || depth(value) > resourceProfile.effectivePolicy.valueDepth
                    || encoder.encode(body).byteLength > maxEnvelopeBytes) {
                  return rejected(failure('resource-exhausted', 'rpc.bridge.request-limit', 'The browser bridge request exceeds a negotiated resource limit.'), stream);
                }
                if (pending.size >= maxPendingRequests) {
                  return rejected(failure('resource-exhausted', 'rpc.bridge.pending-limit', 'The browser bridge request limit was reached.'), stream);
                }
                let resolve = () => {};
                let reject = () => {};
                const promise = stream ? null : new Promise((ok, fail) => { resolve = ok; reject = fail; });
                const state = { requestId, resolve, reject, stream, queue: [], waiters: [], done: false, error: null, signal: options?.signal, cancelSent: false, timer: null };
                const notifyCancel = () => {
                  if (state.done || state.cancelSent) return;
                  state.cancelSent = true;
                  try {
                    window.invokeCSharpAction(JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method: 'bridge.cancel', deadlineUtc: null, payload: { targetRequestId: requestId } }));
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.', 'possibly-dispatched', 'unsafe'));
                  }
                };
                const cancel = () => {
                  if (state.done) return;
                  notifyCancel();
                  finish(state, failure('cancelled', 'rpc.bridge.cancelled', 'The browser bridge request was cancelled.', 'possibly-dispatched', 'unsafe'));
                };
                state.cancel = cancel;
                state.notifyCancel = notifyCancel;
                pending.set(requestId, state);
                const requestedDeadline = Date.parse(normalized.deadlineUtc ?? '');
                const remaining = Number.isFinite(requestedDeadline)
                  ? Math.max(0, requestedDeadline - Date.now())
                  : maxRequestMilliseconds;
                const timeout = Math.min(maxRequestMilliseconds, remaining);
                state.timer = setTimeout(() => {
                  if (state.done) return;
                  notifyCancel();
                  finish(state, failure('deadline-exceeded', 'rpc.call.deadline-exceeded', 'The browser bridge request deadline elapsed.', 'possibly-dispatched', 'unsafe'));
                }, timeout);
                if (!state.done) {
                  try {
                    window.invokeCSharpAction(body);
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.', 'possibly-dispatched', 'unsafe'));
                  }
                }
                if (!state.done) {
                  options?.signal?.addEventListener('abort', cancel, { once: true });
                  if (options?.signal?.aborted) cancel();
                }
                if (!stream) return promise;
                return Object.freeze({
                  [Symbol.asyncIterator]() {
                    return {
                      next() {
                        if (state.queue.length) {
                          const item = state.queue.shift();
                          queuedEventBytes -= item.size;
                          return Promise.resolve({ done: false, value: item.value });
                        }
                        if (state.done) return state.error ? Promise.reject(state.error) : Promise.resolve({ done: true, value: undefined });
                        return new Promise((ok, fail) => state.waiters.push({ ok, fail }));
                      },
                      return() { cancel(); return Promise.resolve({ done: true, value: undefined }); }
                    };
                  }
                });
              };
              Object.defineProperty(window, '__sunderReceive', { configurable: true, value(raw) {
                let message;
                try { message = JSON.parse(raw); } catch { return; }
                if (message?.protocol !== protocol || message?.version !== version || message?.nonce !== nonce) return;
                const state = pending.get(message.requestId);
                if (!state || state.done) return;
                const emittedBytes = encoder.encode(raw).byteLength;
                if (emittedBytes > maxEnvelopeBytes) {
                  state.notifyCancel();
                  finish(state, failure('resource-exhausted', 'rpc.bridge.response-envelope-limit', 'The browser bridge response exceeds its negotiated envelope limit.', 'possibly-dispatched', 'unsafe'));
                  return;
                }
                if (message.type === 'event' || message.type === 'result') {
                  let valueJson;
                  try { valueJson = JSON.stringify(message.value); } catch { valueJson = undefined; }
                  if (typeof valueJson !== 'string'
                      || encoder.encode(valueJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                      || depth(message.value) > resourceProfile.effectivePolicy.valueDepth) {
                    state.notifyCancel();
                    finish(state, failure('resource-exhausted', 'rpc.bridge.response-limit', 'The browser bridge response exceeds a negotiated resource limit.', 'possibly-dispatched', 'unsafe'));
                    return;
                  }
                }
                if (message.type === 'event') {
                  if (!state.stream) {
                    finish(state, failure('protocol', 'rpc.bridge.unexpected-event', 'The browser bridge returned an unexpected stream event.', 'possibly-dispatched', 'unsafe'));
                    return;
                  }
                  const waiter = state.waiters.shift();
                  if (waiter) {
                    waiter.ok({ done: false, value: message.value });
                  } else if (state.queue.length >= maxQueuedEvents || queuedEventBytes + emittedBytes > maxQueuedEventBytes) {
                    state.notifyCancel();
                    finish(state, failure('resource-exhausted', 'rpc.bridge.event-queue-limit', 'The browser bridge event queue limit was reached.', 'possibly-dispatched', 'unsafe'));
                  } else {
                    state.queue.push({ value: message.value, size: emittedBytes });
                    queuedEventBytes += emittedBytes;
                  }
                  return;
                }
                if (message.type === 'result') {
                  if (state.stream) finish(state, failure('protocol', 'rpc.bridge.unexpected-result', 'The browser bridge returned an unexpected unary result.', 'possibly-dispatched', 'unsafe'));
                  else { state.resolve(message.value); finish(state); }
                  return;
                }
                if (message.type === 'complete') {
                  if (state.stream) finish(state);
                  else finish(state, failure('protocol', 'rpc.bridge.unexpected-complete', 'The browser bridge completed without a unary result.', 'possibly-dispatched', 'unsafe'));
                  return;
                }
                if (message.type === 'error') {
                  const detail = message.error ?? {};
                  const dispatchState = detail.dispatchState === 'not-dispatched' ? 'not-dispatched' : 'possibly-dispatched';
                  const retrySafety = detail.retrySafety === 'safe' ? 'safe' : 'unsafe';
                  let details;
                  if (Object.prototype.hasOwnProperty.call(detail, 'details')) {
                    let detailsJson;
                    try { detailsJson = JSON.stringify(detail.details); } catch { detailsJson = undefined; }
                    if (detail.kind !== 'domain'
                        || typeof detailsJson !== 'string'
                        || encoder.encode(detailsJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                        || depth(detail.details) > resourceProfile.effectivePolicy.valueDepth) {
                      finish(state, failure('protocol', 'rpc.bridge.invalid-error-details', 'The browser bridge returned invalid RPC error details.', dispatchState, retrySafety));
                      return;
                    }
                    details = detail.details;
                  }
                  finish(state, failure(detail.kind ?? 'protocol', detail.code ?? 'rpc.bridge.error', detail.message ?? 'Sunder RPC failed.', dispatchState, retrySafety, details));
                  return;
                }
                finish(state, failure('protocol', 'rpc.bridge.message-type', 'The browser bridge returned an unknown message type.', 'possibly-dispatched', 'unsafe'));
              }});
              const rpc = Object.freeze({
                discover: (contractId, options) => send('rpc.discover', { contractId }, options, false),
                watch: (afterRevision, afterSequence, options) => send('rpc.watch', { afterRevision, afterSequence }, options, true),
                invoke: (providerHandle, serviceId, methodId, request, options) => send('rpc.invoke', { providerHandle, serviceId, methodId, request }, options, false),
                subscribe: (providerHandle, serviceId, methodId, request, options) => send('rpc.subscribe', { providerHandle, serviceId, methodId, request }, options, true)
              });
              const api = Object.freeze({
                version,
                resourceProfile,
                rpc,
                openExternal: (url, options) => send('host.openExternal', { url }, options, false),
                getNavigationState: (options) => send('navigation.getState', {}, options, false),
                navigate: (route, options) => send('navigation.navigate', { route }, options, false)
              });
              Object.defineProperty(window, 'sunder', { configurable: false, enumerable: true, writable: false, value: api });
              Object.defineProperty(window, marker, { configurable: false, enumerable: false, writable: false, value: true });
              return true;
            })();
            """);
    }

    private bool TryReadRequest(JsonElement root, out BridgeRequest request, out string error)
    {
        request = default;
        error = "The browser bridge envelope is invalid.";
        var properties = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().ToArray()
            : [];
        if (root.ValueKind != JsonValueKind.Object
            || properties.Length != 8
            || properties.Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != 8
            || properties.Any(static property => property.Name is not
                ("protocol" or "version" or "nonce" or "origin" or "requestId" or "method" or "deadlineUtc" or "payload"))
            || !root.TryGetProperty("protocol", out var protocol)
            || protocol.ValueKind != JsonValueKind.String
            || protocol.GetString() != Protocol
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionValue)
            || versionValue != ProtocolVersion
            || !root.TryGetProperty("nonce", out var nonce)
            || nonce.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("origin", out var origin)
            || origin.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("requestId", out var requestId)
            || requestId.ValueKind != JsonValueKind.String
            || !IsId(requestId.GetString())
            || !root.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String
            || method.GetString() is not { Length: > 0 and <= 64 } methodValue
            || !TryReadDeadline(root, out var deadlineUtc)
            || !root.TryGetProperty("payload", out var payload))
        {
            return false;
        }
        request = new BridgeRequest(
            nonce.GetString()!,
            origin.GetString()!,
            requestId.GetString()!,
            methodValue,
            deadlineUtc,
            payload.Clone());
        error = string.Empty;
        return true;
    }

    private static bool TryReadDeadline(JsonElement root, out DateTimeOffset? deadlineUtc)
    {
        deadlineUtc = null;
        if (!root.TryGetProperty("deadlineUtc", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                value.GetString(),
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }
        deadlineUtc = parsed;
        return true;
    }

    private static JsonElement RequireObject(JsonElement value, string label, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Validation("bridge.payload.object", $"The {label} must be an object.");
        }
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        if (value.EnumerateObject().Any(property => !names.Contains(property.Name)))
        {
            throw Validation("bridge.payload.property", $"The {label} contains an unsupported property.");
        }
        return value;
    }

    private static string RequiredText(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text
            || text.Length > maximum)
        {
            throw Validation("bridge.payload.text", $"Bridge payload property '{name}' is invalid.");
        }
        return text;
    }

    private static string RequiredId(JsonElement root, string name)
    {
        var value = RequiredText(root, name, 128);
        if (!IsId(value))
        {
            throw Validation("bridge.payload.id", $"Bridge payload property '{name}' is not a valid id.");
        }
        return value;
    }

    private static long RequiredNonNegativeInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < 0
            || result > 9_007_199_254_740_991)
        {
            throw Validation("bridge.payload.integer", $"Bridge payload property '{name}' is invalid.");
        }
        return result;
    }

    private static bool IsId(string? value)
        => value is { Length: > 0 and <= 128 }
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private void EnsureBounded(JsonElement value)
    {
        if (!IsBounded(value))
        {
            throw new SunderRpcException(ResourceLimit(
                "rpc.bridge.payload-limit",
                "The browser bridge RPC payload exceeds a negotiated resource limit."));
        }
    }

    private bool IsBounded(JsonElement value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, _boundedJsonOptions).Length
                       <= _limits.EffectivePolicy.MaximumRpcValueBytes
                   && Depth(value) <= _limits.EffectivePolicy.MaximumValueDepth;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int Depth(JsonElement root)
    {
        var maximum = 1;
        var stack = new Stack<(JsonElement Value, int Depth)>();
        stack.Push((root, 1));
        while (stack.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray()) stack.Push((child, item.Depth + 1));
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in item.Value.EnumerateObject()) stack.Push((child.Value, item.Depth + 1));
            }
        }
        return maximum;
    }

    private static SunderRpcError Sanitize(SunderRpcError error)
    {
        var code = error.Code.Length is > 0 and <= 128
                   && error.Code.All(static character => char.IsAsciiLetterOrDigit(character)
                                                               || character is '.' or '-' or '_')
            ? error.Code
            : "rpc.bridge.error";
        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0) message = "The Sunder RPC request failed.";
        if (message.Length > 512) message = message[..512];
        return new SunderRpcError(
            error.Kind,
            code,
            message,
            error.DispatchState,
            error.RetrySafety,
            error.Details);
    }

    private static SunderRpcException WithDispatchMetadata(
        SunderRpcException exception,
        PendingRequest pending)
        => new(new SunderRpcError(
            exception.Error.Kind,
            exception.Error.Code,
            exception.Error.Message,
            pending.DispatchState,
            pending.RetrySafety,
            exception.Error.Details));

    private static object MapError(SunderRpcError error)
    {
        var kind = ErrorKind(error.Kind);
        var dispatchState = error.DispatchState == SunderRpcDispatchState.NotDispatched
            ? "not-dispatched"
            : "possibly-dispatched";
        var retrySafety = error.RetrySafety == SunderRpcRetrySafety.Safe ? "safe" : "unsafe";
        return error.Details is { } details
            ? new { kind, error.Code, error.Message, dispatchState, retrySafety, details }
            : new { kind, error.Code, error.Message, dispatchState, retrySafety };
    }

    private static SunderRpcError ResponseLimit(PendingRequest pending)
        => new(
            SunderRpcErrorKind.ResourceExhausted,
            "rpc.bridge.response-limit",
            "The browser bridge response exceeds its size limit.",
            pending.DispatchState,
            pending.RetrySafety);

    private static SunderRpcError ResourceLimit(string code, string message)
        => new(
            SunderRpcErrorKind.ResourceExhausted,
            code,
            message);

    private async Task RejectRecoveredAsync(
        string? requestId,
        SunderRpcError error,
        string? logMessage = null)
    {
        RejectProtocol(logMessage ?? error.Message);
        if (!IsId(requestId))
        {
            return;
        }

        string nonce;
        lock (_gate)
        {
            if (!_trustedNavigation
                || _currentUri is null
                || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            nonce = _nonce;
        }
        var envelope = new
        {
            protocol = Protocol,
            version = ProtocolVersion,
            nonce,
            requestId,
            type = "error",
            value = (object?)null,
            error = new
            {
                kind = ErrorKind(error.Kind),
                code = error.Code,
                message = error.Message,
                dispatchState = "not-dispatched",
                retrySafety = "safe",
            },
        };
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(envelope, _boundedJsonOptions);
        if (utf8.Length > _limits.MaximumJsonEnvelopeBytes)
        {
            return;
        }
        var json = Encoding.UTF8.GetString(utf8);
        await _webView.InvokeScriptAsync(
                $"window.__sunderReceive?.({JsonSerializer.Serialize(json)});")
            .ConfigureAwait(false);
    }

    private static bool TryReadSafeRequestId(JsonElement root, out string? requestId)
    {
        requestId = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var matches = root.EnumerateObject()
            .Where(static property => property.NameEquals("requestId"))
            .ToArray();
        if (matches.Length != 1
            || matches[0].Value.ValueKind != JsonValueKind.String
            || !IsId(matches[0].Value.GetString()))
        {
            return false;
        }
        requestId = matches[0].Value.GetString();
        return true;
    }

    private static string? TryRecoverRequestId(string body)
    {
        const int maximumPrefixCharacters = 16 * 1024;
        var prefix = body.AsSpan(0, Math.Min(body.Length, maximumPrefixCharacters));
        byte[] utf8;
        try
        {
            utf8 = Encoding.UTF8.GetBytes(prefix.ToString());
        }
        catch (EncoderFallbackException)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            }));
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName
                    || reader.CurrentDepth != 1
                    || !reader.ValueTextEquals("requestId"u8)
                    || !reader.Read()
                    || reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }
                var value = reader.GetString();
                return IsId(value) ? value : null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string ErrorKind(SunderRpcErrorKind kind)
        => kind switch
        {
            SunderRpcErrorKind.Domain => "domain",
            SunderRpcErrorKind.PermissionDenied => "permission-denied",
            SunderRpcErrorKind.NotFound => "not-found",
            SunderRpcErrorKind.StaleEndpoint => "stale-endpoint",
            SunderRpcErrorKind.Validation => "validation",
            SunderRpcErrorKind.DeadlineExceeded => "deadline-exceeded",
            SunderRpcErrorKind.Cancelled => "cancelled",
            SunderRpcErrorKind.ResourceExhausted => "resource-exhausted",
            SunderRpcErrorKind.Unavailable => "unavailable",
            SunderRpcErrorKind.ProviderFaulted => "provider-faulted",
            _ => "protocol",
        };

    private static SunderRpcException Validation(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Validation, code, message));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreateNonce() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static void RejectProtocol(string message)
        => AppSessionLog.WriteInfo($"Rejected package browser bridge message: {message}", visibleInDeveloperLog: false);

    private readonly record struct BridgeRequest(
        string Nonce,
        string Origin,
        string RequestId,
        string Method,
        DateTimeOffset? DeadlineUtc,
        JsonElement Payload);

    private sealed record BridgeInvocation(
        string ProviderHandle,
        string ServiceId,
        string MethodId,
        JsonElement Request);

    private sealed class PendingRequest : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _cancellation = new();
        private CancellationTokenRegistration _requestRegistration;
        private CancellationTokenRegistration _lifetimeRegistration;
        private CancellationTokenRegistration _deadlineRegistration;
        private int _cancellationReason;
        private int _dispatchState;
        private int _retrySafety;
        private int _completed;
        private int _disposed;

        private PendingRequest(
            long documentGeneration,
            TimeSpan timeout)
        {
            DocumentGeneration = documentGeneration;
            _deadline = new CancellationTokenSource(timeout);
        }

        public long DocumentGeneration { get; }

        public CancellationToken Token => _cancellation.Token;

        public bool TimedOut => Volatile.Read(ref _cancellationReason) == (int)CancellationReason.Deadline;

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public SunderRpcDispatchState DispatchState
            => (SunderRpcDispatchState)Volatile.Read(ref _dispatchState);

        public SunderRpcRetrySafety RetrySafety
            => (SunderRpcRetrySafety)Volatile.Read(ref _retrySafety);

        public static PendingRequest Create(
            long documentGeneration,
            CancellationToken request,
            CancellationToken lifetime,
            TimeSpan timeout,
            DateTimeOffset? requestedDeadline)
        {
            var effectiveTimeout = timeout;
            if (requestedDeadline is { } deadlineUtc)
            {
                var remaining = deadlineUtc - DateTimeOffset.UtcNow;
                if (remaining < effectiveTimeout) effectiveTimeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            var pending = new PendingRequest(documentGeneration, effectiveTimeout);
            pending._requestRegistration = request.Register(
                static state => ((PendingRequest)state!).TryCancel(CancellationReason.Cancelled),
                pending);
            pending._lifetimeRegistration = lifetime.Register(
                static state => ((PendingRequest)state!).TryCancel(CancellationReason.Cancelled),
                pending);
            pending._deadlineRegistration = pending._deadline.Token.Register(
                static state => ((PendingRequest)state!).TryCancel(CancellationReason.Deadline),
                pending);
            return pending;
        }

        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;

        public void MarkPossiblyDispatched(SunderRpcRetrySafety retrySafety)
        {
            Volatile.Write(ref _retrySafety, (int)retrySafety);
            Volatile.Write(ref _dispatchState, (int)SunderRpcDispatchState.PossiblyDispatched);
        }

        public void Cancel()
        {
            TryCancel(CancellationReason.Cancelled);
        }

        private void TryCancel(CancellationReason reason)
        {
            if (Interlocked.CompareExchange(ref _cancellationReason, (int)reason, (int)CancellationReason.None)
                != (int)CancellationReason.None)
            {
                return;
            }
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race with navigation or bridge disposal.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _requestRegistration.Dispose();
            _lifetimeRegistration.Dispose();
            _deadlineRegistration.Dispose();
            _deadline.Dispose();
            _cancellation.Dispose();
        }

        private enum CancellationReason
        {
            None,
            Cancelled,
            Deadline,
        }
    }
}
