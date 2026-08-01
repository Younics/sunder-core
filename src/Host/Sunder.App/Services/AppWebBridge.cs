using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppWebBridge : IAsyncDisposable
{
    internal const string Protocol = "sunder.bridge.v1";
    internal const int ProtocolVersion = 1;
    internal const int MaxMessageBytes = 1024 * 1024;
    internal const int MaxMessageDepth = 32;
    internal const int MaxPendingRequests = 64;
    private const int MaxRememberedRequestIds = 2048;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAppWebView _webView;
    private readonly AppWebContentServer _contentServer;
    private readonly IAppWebRpcClient _rpc;
    private readonly AppWebRpcContractCatalog _contracts;
    private readonly ExternalBrowserService _externalBrowser;
    private readonly Action<Uri> _navigate;
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
        Action<Uri> navigate)
    {
        _webView = webView;
        _contentServer = contentServer;
        _rpc = rpc;
        _contracts = contracts;
        _externalBrowser = externalBrowser;
        _navigate = navigate;
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
        if (body is null || Encoding.UTF8.GetByteCount(body) > MaxMessageBytes)
        {
            RejectProtocol("The browser bridge message exceeds its size limit.");
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = MaxMessageDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException)
        {
            RejectProtocol("The browser bridge message is invalid JSON.");
            return;
        }
        using (document)
        {
            if (!TryReadRequest(document.RootElement, out var request, out var error))
            {
                RejectProtocol(error);
                return;
            }

            if (request.Method == "bridge.cancel")
            {
                CancelRequest(request);
                return;
            }

            PendingRequest pending;
            lock (_gate)
            {
                if (!_trustedNavigation
                    || _currentUri is null
                    || !string.Equals(request.Nonce, _nonce, StringComparison.Ordinal)
                    || !string.Equals(request.Origin, _origin, StringComparison.Ordinal))
                {
                    RejectProtocol("The browser bridge origin or nonce is invalid.");
                    return;
                }
                if (!_rememberedIds.Add(request.RequestId))
                {
                    RejectProtocol($"The browser bridge reused request id '{request.RequestId}'.");
                    return;
                }
                _rememberedOrder.Enqueue(request.RequestId);
                while (_rememberedOrder.Count > MaxRememberedRequestIds)
                {
                    _rememberedIds.Remove(_rememberedOrder.Dequeue());
                }
                if (_pending.Count >= MaxPendingRequests)
                {
                    pending = PendingRequest.Create(
                        _documentGeneration,
                        cancellationToken,
                        _lifetime.Token,
                        RequestTimeout);
                    _pending.Add(request.RequestId, pending);
                    _ = FinishErrorAsync(
                        request.RequestId,
                        pending,
                        new SunderRpcError(
                            SunderRpcErrorKind.ResourceExhausted,
                            "rpc.bridge.pending-limit",
                            "The browser bridge request limit was reached."));
                    return;
                }
                pending = PendingRequest.Create(
                    _documentGeneration,
                    cancellationToken,
                    _lifetime.Token,
                    RequestTimeout);
                _pending.Add(request.RequestId, pending);
            }

            await ExecuteAsync(request, pending).ConfigureAwait(false);
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
                    var response = await _rpc.InvokeAsync(
                        provider.Endpoint.Value,
                        invocation.ServiceId,
                        invocation.MethodId,
                        invocation.Request,
                        invocation.DeadlineUtc,
                        pending.Token).ConfigureAwait(false);
                    EnsureBounded(response);
                    _contracts.ValidateOutput(provider, method, response);
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
                    await foreach (var item in _rpc.SubscribeAsync(
                                       provider.Endpoint.Value,
                                       invocation.ServiceId,
                                       invocation.MethodId,
                                       invocation.Request,
                                       invocation.DeadlineUtc,
                                       pending.Token).WithCancellation(pending.Token).ConfigureAwait(false))
                    {
                        EnsureBounded(item);
                        _contracts.ValidateOutput(provider, method, item);
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
                    "rpc.bridge.timeout",
                    "The browser bridge request timed out.")
                : new SunderRpcError(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.bridge.cancelled",
                    "The browser bridge request was cancelled.");
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
                    "The browser bridge request could not be completed.")).ConfigureAwait(false);
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
                RejectProtocol("A browser bridge cancellation has an invalid origin or nonce.");
                return;
            }
            if (!_pending.TryGetValue(target, out var pending))
            {
                RejectProtocol($"The browser bridge cancellation references unknown request '{target}'.");
                return;
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
            "request",
            "deadlineUtc");
        var deadlineText = OptionalText(payload, "deadlineUtc", 64);
        DateTimeOffset? deadline = null;
        if (deadlineText is not null
            && (!DateTimeOffset.TryParseExact(
                    deadlineText,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed)
                || parsed.Offset != TimeSpan.Zero))
        {
            throw Validation("rpc.bridge.deadline", "The RPC deadline must be a UTC ISO-8601 timestamp.");
        }
        else if (deadlineText is not null)
        {
            deadline = DateTimeOffset.ParseExact(
                deadlineText,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
        }
        var request = payload.GetProperty("request").Clone();
        EnsureBounded(request);
        return new BridgeInvocation(
            RequiredId(payload, "providerHandle"),
            RequiredText(payload, "serviceId", 128),
            RequiredText(payload, "methodId", 128),
            request,
            deadline);
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
            contractSha256 = provider.ContractSha256,
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
                await SendAsync(requestId, pending, type, value, error).ConfigureAwait(false);
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
        await SendAsync(requestId, pending, "event", value, error: null).ConfigureAwait(false);
    }

    private async Task SendAsync(
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
            type,
            value,
            error = error is null ? null : new
            {
                kind = ErrorKind(error.Kind),
                code = error.Code,
                message = error.Message,
            },
        };
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            if (error is not null)
            {
                return;
            }
            await SendAsync(
                requestId,
                pending,
                "error",
                value: null,
                new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.bridge.response-limit",
                    "The browser bridge response exceeds its size limit.")).ConfigureAwait(false);
            return;
        }
        var script = $"window.__sunderReceive?.({JsonSerializer.Serialize(json)});";
        await _webView.InvokeScriptAsync(script).ConfigureAwait(false);
    }

    private string CreateBootstrapScript(string sessionNonce)
    {
        var protocol = JsonSerializer.Serialize(Protocol);
        var nonce = JsonSerializer.Serialize(sessionNonce);
        var origin = JsonSerializer.Serialize(_origin);
        return $$$"""
            (() => {
              'use strict';
              const protocol = {{{protocol}}};
              const version = 1;
              const nonce = {{{nonce}}};
              const expectedOrigin = {{{origin}}};
              if (location.origin !== expectedOrigin || typeof window.invokeCSharpAction !== 'function') return false;
              const marker = `__sunderBridge_${nonce}`;
              if (window[marker] === true && window.sunder?.version === version && typeof window.__sunderReceive === 'function') return true;
              if (Object.prototype.hasOwnProperty.call(window, 'sunder')) return false;
              let sequence = 0;
              const pending = new Map();
              const maxQueuedEvents = 32;
              const maxQueuedCharacters = 4 * 1024 * 1024;
              let queuedCharacters = 0;
              const failure = (kind, code, message) => Object.assign(new Error(message), {
                name: 'SunderRpcError',
                error: Object.freeze({ kind, code, message })
              });
              const finish = (state, error) => {
                if (state.done) return;
                state.done = true;
                state.error = error;
                state.signal?.removeEventListener('abort', state.cancel);
                pending.delete(state.requestId);
                for (const item of state.queue.splice(0)) queuedCharacters -= item.size;
                for (const waiter of state.waiters.splice(0)) error ? waiter.fail(error) : waiter.ok({ done: true, value: undefined });
                if (!state.stream && error) state.reject(error);
              };
              const send = (method, payload, options, stream) => {
                const requestId = `b_${++sequence}`;
                let resolve = () => {};
                let reject = () => {};
                const promise = stream ? null : new Promise((ok, fail) => { resolve = ok; reject = fail; });
                const state = { requestId, resolve, reject, stream, queue: [], waiters: [], done: false, error: null, signal: options?.signal, cancelSent: false };
                const cancel = () => {
                  if (state.done || state.cancelSent) return;
                  state.cancelSent = true;
                  try {
                    window.invokeCSharpAction(JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method: 'bridge.cancel', payload: { targetRequestId: requestId } }));
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.'));
                  }
                };
                state.cancel = cancel;
                pending.set(requestId, state);
                let body;
                try {
                  body = JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method, payload });
                } catch {
                  finish(state, failure('validation', 'rpc.bridge.serialization', 'The browser bridge request is not valid JSON.'));
                }
                if (!state.done) {
                  try {
                    window.invokeCSharpAction(body);
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.'));
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
                          queuedCharacters -= item.size;
                          return Promise.resolve({ done: false, value: item.value });
                        }
                        if (state.done) return state.error ? Promise.reject(state.error) : Promise.resolve({ done: true, value: undefined });
                        return new Promise((ok, fail) => state.waiters.push({ ok, fail }));
                      },
                      return() { cancel(); finish(state); return Promise.resolve({ done: true, value: undefined }); }
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
                if (message.type === 'event') {
                  if (!state.stream) {
                    finish(state, failure('protocol', 'rpc.bridge.unexpected-event', 'The browser bridge returned an unexpected stream event.'));
                    return;
                  }
                  const waiter = state.waiters.shift();
                  if (waiter) {
                    waiter.ok({ done: false, value: message.value });
                  } else if (state.queue.length >= maxQueuedEvents || queuedCharacters + raw.length > maxQueuedCharacters) {
                    state.cancel();
                    finish(state, failure('resource-exhausted', 'rpc.bridge.event-queue-limit', 'The browser bridge event queue limit was reached.'));
                  } else {
                    state.queue.push({ value: message.value, size: raw.length });
                    queuedCharacters += raw.length;
                  }
                  return;
                }
                if (message.type === 'result') {
                  if (state.stream) finish(state, failure('protocol', 'rpc.bridge.unexpected-result', 'The browser bridge returned an unexpected unary result.'));
                  else { state.resolve(message.value); finish(state); }
                  return;
                }
                if (message.type === 'complete') {
                  if (state.stream) finish(state);
                  else finish(state, failure('protocol', 'rpc.bridge.unexpected-complete', 'The browser bridge completed without a unary result.'));
                  return;
                }
                if (message.type === 'error') {
                  const detail = message.error ?? {};
                  finish(state, failure(detail.kind ?? 'protocol', detail.code ?? 'rpc.bridge.error', detail.message ?? 'Sunder RPC failed.'));
                  return;
                }
                finish(state, failure('protocol', 'rpc.bridge.message-type', 'The browser bridge returned an unknown message type.'));
              }});
              const rpc = Object.freeze({
                discover: (contractId, options) => send('rpc.discover', { contractId }, options, false),
                watch: (afterRevision, afterSequence, options) => send('rpc.watch', { afterRevision, afterSequence }, options, true),
                invoke: (providerHandle, serviceId, methodId, request, options) => send('rpc.invoke', { providerHandle, serviceId, methodId, request, deadlineUtc: options?.deadline?.toISOString?.() ?? null }, options, false),
                subscribe: (providerHandle, serviceId, methodId, request, options) => send('rpc.subscribe', { providerHandle, serviceId, methodId, request, deadlineUtc: options?.deadline?.toISOString?.() ?? null }, options, true)
              });
              const api = Object.freeze({
                version,
                rpc,
                openExternal: (url, options) => send('host.openExternal', { url }, options, false),
                getNavigationState: (options) => send('navigation.getState', {}, options, false),
                navigate: (route, options) => send('navigation.navigate', { route }, options, false)
              });
              Object.defineProperty(window, 'sunder', { configurable: false, enumerable: true, writable: false, value: api });
              Object.defineProperty(window, marker, { configurable: false, enumerable: false, writable: false, value: true });
              return true;
            })();
            """;
    }

    private bool TryReadRequest(JsonElement root, out BridgeRequest request, out string error)
    {
        request = default;
        error = "The browser bridge envelope is invalid.";
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(static property => property.Name is not
                ("protocol" or "version" or "nonce" or "origin" or "requestId" or "method" or "payload"))
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
            || !root.TryGetProperty("payload", out var payload))
        {
            return false;
        }
        request = new BridgeRequest(
            nonce.GetString()!,
            origin.GetString()!,
            requestId.GetString()!,
            methodValue,
            payload.Clone());
        error = string.Empty;
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

    private static string? OptionalText(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequiredText(root, name, maximum);
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

    private static void EnsureBounded(JsonElement value)
    {
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxMessageBytes || Depth(value) > MaxMessageDepth)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.bridge.payload-limit",
                "The browser bridge RPC payload exceeds a limit."));
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
        return new SunderRpcError(error.Kind, code, message);
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
        JsonElement Payload);

    private sealed record BridgeInvocation(
        string ProviderHandle,
        string ServiceId,
        string MethodId,
        JsonElement Request,
        DateTimeOffset? DeadlineUtc);

    private sealed class PendingRequest : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationToken _timeoutToken;
        private readonly CancellationToken _token;
        private int _completed;
        private int _disposed;

        private PendingRequest(
            long documentGeneration,
            CancellationTokenSource timeout,
            CancellationTokenSource linked)
        {
            DocumentGeneration = documentGeneration;
            _timeout = timeout;
            _linked = linked;
            _timeoutToken = timeout.Token;
            _token = linked.Token;
        }

        public long DocumentGeneration { get; }

        public CancellationToken Token => _token;

        public bool TimedOut => _timeoutToken.IsCancellationRequested;

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public static PendingRequest Create(
            long documentGeneration,
            CancellationToken request,
            CancellationToken lifetime,
            TimeSpan timeout)
        {
            var deadline = new CancellationTokenSource(timeout);
            return new PendingRequest(
                documentGeneration,
                deadline,
                CancellationTokenSource.CreateLinkedTokenSource(request, lifetime, deadline.Token));
        }

        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;

        public void Cancel()
        {
            try
            {
                _linked.Cancel();
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
            _linked.Dispose();
            _timeout.Dispose();
        }
    }
}
