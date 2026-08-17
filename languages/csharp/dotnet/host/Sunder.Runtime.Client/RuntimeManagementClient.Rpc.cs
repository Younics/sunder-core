using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    private static readonly JsonSerializerOptions RpcMetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RpcTransportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = RuntimeResourcePolicies.ProtocolCeilings.MaximumValueDepth
                   + RuntimeClientResourceLimits.EnvelopeDepthOverhead,
    };
    private const string RpcContentMetadataHeader = "X-Sunder-Rpc-Content-Metadata";

    public Task<RuntimeRpcCatalogSnapshot> GetRpcCatalogAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcCatalogSnapshot>(
            "rpc/catalog",
            RuntimeProtocolFeatures.SchemaFirstRpcV2,
            token);

    public Task<RuntimeRpcCatalogEventPage> GetRpcCatalogEventsAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcCatalogEventPage>(
            $"rpc/catalog/events?afterRevision={afterRevision}&afterSequence={afterSequence}",
            RuntimeProtocolFeatures.SchemaFirstRpcV2,
            token);

    public Task<RuntimeRpcPermissionSnapshot> GetRpcPermissionsAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcPermissionSnapshot>(
            "rpc/permissions",
            RuntimeProtocolFeatures.RpcPermissionsV2,
            token);

    public Task<RuntimeRpcPermissionSnapshot> GrantRpcPermissionAsync(
        RuntimeRpcPermissionKey key,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcPermissionUpdateRequest, RuntimeRpcPermissionSnapshot>(
            "rpc/permissions/grant",
            new RuntimeRpcPermissionUpdateRequest(key),
            RuntimeProtocolFeatures.RpcPermissionsV2,
            token);

    public Task<RuntimeRpcPermissionSnapshot> RevokeRpcPermissionAsync(
        RuntimeRpcPermissionKey key,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcPermissionUpdateRequest, RuntimeRpcPermissionSnapshot>(
            "rpc/permissions/revoke",
            new RuntimeRpcPermissionUpdateRequest(key),
            RuntimeProtocolFeatures.RpcPermissionsV2,
            token);

    public Task<RuntimeRpcAppSessionDescriptor> OpenAppRpcSessionAsync(
        RuntimeRpcAppSessionOpenRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppSessionOpenRequest, RuntimeRpcAppSessionDescriptor>(
            "rpc/app-sessions/open",
            request,
            RuntimeProtocolFeatures.AppWebRpcV2,
            token);

    public async Task CloseAppRpcSessionAsync(
        string sessionId,
        CancellationToken token = default)
    {
        using var request = CreateRpcRequest(
            "rpc/app-sessions/close",
            new RuntimeRpcAppSessionCloseRequest(sessionId));
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeRpcAppCallScopeDescriptor> OpenAppRpcCallScopeAsync(
        RuntimeRpcAppCallScopeOpenRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppCallScopeOpenRequest, RuntimeRpcAppCallScopeDescriptor>(
            "rpc/app/call-scopes/open",
            request,
            RuntimeProtocolFeatures.AppWebRpcV2,
            token);

    public async Task CloseAppRpcCallScopeAsync(
        RuntimeRpcAppCallScopeCloseRequest closeRequest,
        CancellationToken token = default)
    {
        using var request = CreateRpcRequest("rpc/app/call-scopes/close", closeRequest);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public async Task<RuntimeRpcAppDiscoverResponse> DiscoverAppRpcAsync(
        RuntimeRpcAppDiscoverRequest request,
        CancellationToken token = default)
    {
        var response = await PostAsync<RuntimeRpcAppDiscoverRequest, RuntimeRpcAppDiscoverResponse>(
            "rpc/app/discover",
            request,
            RuntimeProtocolFeatures.AppWebRpcV2,
            token).ConfigureAwait(false);
        EnsureRpcErrorDetails(response.Error, "Runtime RPC discovery error details");
        return response;
    }

    public async Task<RuntimeRpcAppProviderResponse> GetAppRpcProviderAsync(
        RuntimeRpcAppProviderRequest request,
        CancellationToken token = default)
    {
        var response = await PostAsync<RuntimeRpcAppProviderRequest, RuntimeRpcAppProviderResponse>(
            "rpc/app/provider",
            request,
            RuntimeProtocolFeatures.AppWebRpcV2,
            token).ConfigureAwait(false);
        EnsureRpcErrorDetails(response.Error, "Runtime RPC provider error details");
        return response;
    }

    public Task<RuntimeRpcAppInvariantViolationResponse> TryReportAppRpcInvariantViolationAsync(
        RuntimeRpcAppInvariantViolationRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync();

        async Task<RuntimeRpcAppInvariantViolationResponse> SendAsync()
        {
            var response = await PostAsync<RuntimeRpcAppInvariantViolationRequest, RuntimeRpcAppInvariantViolationResponse>(
            "rpc/app/invariant-violation",
            request with { ExceptionMessage = SanitizeInvariantMessage(request.ExceptionMessage) },
            RuntimeProtocolFeatures.AppWebRpcV2,
            token).ConfigureAwait(false);
            EnsureRpcErrorDetails(response.Error, "Runtime RPC invariant error details");
            return response;
        }
    }

    public async Task<RuntimeRpcAppInvokeResponse> InvokeAppRpcAsync(
        RuntimeRpcAppInvokeRequest request,
        CancellationToken token = default)
    {
        using var message = CreateRpcRequest("rpc/app/invoke", request);
        using var httpResponse = await _httpClient.SendAsync(message, token).ConfigureAwait(false);
        var result = await _responses.ReadRequiredJsonAsync<RuntimeRpcAppInvokeResponse>(httpResponse, token)
            .ConfigureAwait(false);
        if (result.Value is { } value)
        {
            _transport.ResourceLimits.EnsureRpcValue(value, "Runtime RPC result value");
        }
        EnsureRpcErrorDetails(result.Error, "Runtime RPC invocation error details");
        return result;
    }

    public IAsyncEnumerable<RuntimeRpcAppCatalogFrame> WatchAppRpcAsync(
        RuntimeRpcAppWatchRequest request,
        CancellationToken token = default)
        => ReadRpcStreamAsync<RuntimeRpcAppWatchRequest, RuntimeRpcAppCatalogFrame>(
            "rpc/app/watch",
            request,
            token);

    public IAsyncEnumerable<RuntimeRpcAppSubscriptionFrame> SubscribeAppRpcAsync(
        RuntimeRpcAppInvokeRequest request,
        CancellationToken token = default)
        => ReadRpcStreamAsync<RuntimeRpcAppInvokeRequest, RuntimeRpcAppSubscriptionFrame>(
            "rpc/app/subscribe",
            request,
            token);

    public async Task<RuntimeRpcAppContentRegisterResponse> RegisterAppRpcContentAsync(
        RuntimeRpcAppContentRegisterMetadata metadata,
        Stream source,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CreateUri("rpc/app/call-scopes/content/register"))
        {
            Content = new StreamContent(new NonDisposingReadStream(source)),
        };
        if (metadata.Length is { } length) request.Content.Headers.ContentLength = length;
        request.Headers.TryAddWithoutValidation(
            RpcContentMetadataHeader,
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, RpcMetadataJsonOptions)));
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { RuntimeProtocolFeatures.AppWebRpcV2 });
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        var result = await _responses.ReadRequiredJsonAsync<RuntimeRpcAppContentRegisterResponse>(response, token)
            .ConfigureAwait(false);
        EnsureRpcErrorDetails(result.Error, "Runtime RPC content registration error details");
        return result;
    }

    public async Task<RuntimeRpcAppContentOpenResult> OpenAppRpcContentAsync(
        RuntimeRpcAppContentOpenRequest openRequest,
        CancellationToken token = default)
    {
        using var request = CreateRpcRequest("rpc/app/call-scopes/content/open", openRequest);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = await _responses.ReadJsonAsync<RuntimeRpcErrorDescriptor>(response, token)
                    .ConfigureAwait(false);
                EnsureRpcErrorDetails(error, "Runtime RPC content-open error details");
                return new RuntimeRpcAppContentOpenResult(null, error);
            }
            finally
            {
                response.Dispose();
            }
        }
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return new RuntimeRpcAppContentOpenResult(
                new RpcResponseReadStream(stream, response),
                null);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRpcRequest<TRequest>(string path, TRequest payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(path))
        {
            Content = JsonContent.Create(payload, options: RpcTransportJsonOptions),
        };
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { RuntimeProtocolFeatures.AppWebRpcV2 });
        if (payload is RuntimeRpcAppInvokeRequest invocation)
        {
            request.Options.Set(RuntimeAuthenticatedHttpMessageHandler.RpcValueKey, invocation.Request);
        }
        return request;
    }

    private static string SanitizeInvariantMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The provider violated an orchestrator invariant.";
        var sanitized = new string(message.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        if (sanitized.Length == 0) return "The provider violated an orchestrator invariant.";
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private async IAsyncEnumerable<TFrame> ReadRpcStreamAsync<TRequest, TFrame>(
        string path,
        TRequest payload,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var request = CreateRpcRequest(path, payload);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var limits = _transport.ResourceLimits;
        var maximumFrameBytes = Math.Min(
            _transport.Policy.MaxStreamEventBytes,
            limits.MaximumJsonEnvelopeBytes);
        var reader = new RuntimeBoundedUtf8LineReader(stream);
        while (await reader.ReadLineAsync(maximumFrameBytes, token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            var frame = JsonSerializer.Deserialize<TFrame>(line, limits.CreateJsonOptions())
                        ?? throw new InvalidDataException("Runtime RPC stream returned an empty frame.");
            if (frame is RuntimeRpcAppSubscriptionFrame { Event: { } item })
            {
                limits.EnsureRpcValue(item, "Runtime RPC stream event value");
            }
            var error = frame switch
            {
                RuntimeRpcAppCatalogFrame catalog => catalog.Error,
                RuntimeRpcAppSubscriptionFrame subscription => subscription.Error,
                _ => null,
            };
            EnsureRpcErrorDetails(error, "Runtime RPC stream error details");
            yield return frame;
        }
    }

    private void EnsureRpcErrorDetails(RuntimeRpcErrorDescriptor? error, string label)
    {
        if (error?.Details is not { } details) return;
        if (error.Kind != RuntimeRpcErrorKind.Domain)
        {
            throw new InvalidDataException("The Runtime returned details on a non-domain RPC error.");
        }
        _transport.ResourceLimits.EnsureRpcValue(details, label);
    }
}

public sealed record RuntimeRpcAppContentOpenResult(
    Stream? Content,
    RuntimeRpcErrorDescriptor? Error);

internal sealed class NonDisposingReadStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RpcResponseReadStream(Stream inner, HttpResponseMessage response) : Stream
{
    private int _disposed;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
