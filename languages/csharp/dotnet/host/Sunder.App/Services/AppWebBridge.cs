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
    internal const string Protocol = "sunder.bridge.v1";
    internal const int ProtocolVersion = 1;
    private readonly IAppWebView _webView;
    private readonly AppWebContentServer _contentServer;
    private readonly IAppWebRpcClient _rpc;
    private readonly AppWebRpcContractCatalog _contracts;
    private readonly ExternalBrowserService _externalBrowser;
    private readonly Action<Uri> _navigate;
    private readonly RuntimeClientResourceLimits _limits;
    private readonly JsonSerializerOptions _boundedJsonOptions;
    private readonly object _gate = new();
    private readonly AppWebPendingRequestRegistry _pendingRequests;
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
        _pendingRequests = new AppWebPendingRequestRegistry(_gate);
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
            lock (_gate) return _pendingRequests.Count;
        }
    }

    public void NavigationStarting()
    {
        AppWebPendingRequest[] requests;
        lock (_gate)
        {
            _trustedNavigation = false;
            _currentUri = null;
            _documentGeneration = checked(_documentGeneration + 1);
            _nonce = CreateNonce();
            requests = _pendingRequests.DetachAll();
            _providerHandles.Clear();
            _handlesByEndpoint.Clear();
        }
        AppWebPendingRequestRegistry.Cancel(requests);
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
            script = AppWebBootstrapScriptGenerator.Generate(
                Protocol,
                ProtocolVersion,
                _origin,
                _nonce,
                _limits);
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

            AppWebPendingRequest? pending = null;
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
                else
                {
                    var reservation = _pendingRequests.TryReserve(
                        request.RequestId,
                        () => CreatePendingRequest(
                            _documentGeneration,
                            cancellationToken,
                            request.DeadlineUtc),
                        out pending);
                    if (reservation == AppWebPendingRequestRegistry.ReservationOutcome.RequestIdReused)
                    {
                        protocolRejection = $"The browser bridge reused request id '{request.RequestId}'.";
                        immediateError = new SunderRpcError(
                            SunderRpcErrorKind.Protocol,
                            "rpc.bridge.request-id-reused",
                            "The browser bridge request id was already used.");
                    }
                    else if (reservation == AppWebPendingRequestRegistry.ReservationOutcome.CapacityReached)
                    {
                        immediateError = new SunderRpcError(
                            SunderRpcErrorKind.ResourceExhausted,
                            "rpc.bridge.pending-limit",
                            "The browser bridge request limit was reached.");
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
        AppWebPendingRequest[] requests;
        lock (_gate)
        {
            _trustedNavigation = false;
            _currentUri = null;
            requests = _pendingRequests.DetachAll();
            _providerHandles.Clear();
            _handlesByEndpoint.Clear();
        }
        AppWebPendingRequestRegistry.CancelAndDispose(requests);
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task ExecuteAsync(BridgeRequest request, AppWebPendingRequest pending)
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
            if (!_pendingRequests.TryCancel(target))
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Protocol,
                    "rpc.bridge.cancel-target",
                    "The browser bridge cancellation target is not pending."));
            }
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
                ?? throw new InvalidDataException("The Runtime provider is missing its ABI identity."),
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

    private Task FinishResultAsync(string requestId, AppWebPendingRequest pending, object? value)
        => FinishAsync(requestId, pending, "result", value, error: null);

    private Task FinishCompleteAsync(string requestId, AppWebPendingRequest pending)
        => FinishAsync(requestId, pending, "complete", value: null, error: null);

    private Task FinishErrorAsync(string requestId, AppWebPendingRequest pending, SunderRpcError error)
        => FinishAsync(requestId, pending, "error", value: null, error);

    private async Task FinishAsync(
        string requestId,
        AppWebPendingRequest pending,
        string type,
        object? value,
        SunderRpcError? error)
    {
        var send = false;
        lock (_gate)
        {
            send = _pendingRequests.TryComplete(requestId, pending);
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

    private async Task SendEventAsync(string requestId, AppWebPendingRequest pending, object? value)
    {
        lock (_gate)
        {
            if (!_pendingRequests.IsCurrent(requestId, pending)
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
        AppWebPendingRequest pending,
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
        AppWebPendingRequest pending)
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

    private static SunderRpcError ResponseLimit(AppWebPendingRequest pending)
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

    private AppWebPendingRequest CreatePendingRequest(
        long documentGeneration,
        CancellationToken request,
        DateTimeOffset? requestedDeadline)
    {
        var effectiveTimeout = RuntimeClientResourceLimits.BrowserRequestTimeout;
        if (requestedDeadline is { } deadlineUtc)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining < effectiveTimeout)
            {
                effectiveTimeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
        var deadline = new CancellationTokenSource(effectiveTimeout);
        try
        {
            return new AppWebPendingRequest(
                documentGeneration,
                request,
                _lifetime.Token,
                deadline);
        }
        catch
        {
            deadline.Dispose();
            throw;
        }
    }

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
}
