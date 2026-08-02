using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    private static readonly JsonSerializerOptions RpcStreamJsonOptions = new(JsonSerializerDefaults.Web);
    private const string RpcContentMetadataHeader = "X-Sunder-Rpc-Content-Metadata";

    public Task<RuntimeRpcCatalogSnapshot> GetRpcCatalogAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcCatalogSnapshot>(
            "rpc/catalog",
            RuntimeProtocolFeatures.SchemaFirstRpcV1,
            token);

    public Task<RuntimeRpcCatalogEventPage> GetRpcCatalogEventsAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcCatalogEventPage>(
            $"rpc/catalog/events?afterRevision={afterRevision}&afterSequence={afterSequence}",
            RuntimeProtocolFeatures.SchemaFirstRpcV1,
            token);

    public Task<RuntimeRpcPermissionSnapshot> GetRpcPermissionsAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeRpcPermissionSnapshot>(
            "rpc/permissions",
            RuntimeProtocolFeatures.RpcPermissionsV1,
            token);

    public Task<RuntimeRpcPermissionSnapshot> GrantRpcPermissionAsync(
        string callerPackageId,
        string contractId,
        string action,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcPermissionUpdateRequest, RuntimeRpcPermissionSnapshot>(
            "rpc/permissions/grant",
            new RuntimeRpcPermissionUpdateRequest(callerPackageId, contractId, action),
            RuntimeProtocolFeatures.RpcPermissionsV1,
            token);

    public Task<RuntimeRpcPermissionSnapshot> RevokeRpcPermissionAsync(
        string callerPackageId,
        string contractId,
        string action,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcPermissionUpdateRequest, RuntimeRpcPermissionSnapshot>(
            "rpc/permissions/revoke",
            new RuntimeRpcPermissionUpdateRequest(callerPackageId, contractId, action),
            RuntimeProtocolFeatures.RpcPermissionsV1,
            token);

    public Task<RuntimeRpcAppSessionDescriptor> OpenAppRpcSessionAsync(
        RuntimeRpcAppSessionOpenRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppSessionOpenRequest, RuntimeRpcAppSessionDescriptor>(
            "rpc/app-sessions/open",
            request,
            RuntimeProtocolFeatures.AppWebRpcV1,
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
            RuntimeProtocolFeatures.AppWebRpcV1,
            token);

    public async Task CloseAppRpcCallScopeAsync(
        RuntimeRpcAppCallScopeCloseRequest closeRequest,
        CancellationToken token = default)
    {
        using var request = CreateRpcRequest("rpc/app/call-scopes/close", closeRequest);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeRpcAppDiscoverResponse> DiscoverAppRpcAsync(
        RuntimeRpcAppDiscoverRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppDiscoverRequest, RuntimeRpcAppDiscoverResponse>(
            "rpc/app/discover",
            request,
            RuntimeProtocolFeatures.AppWebRpcV1,
            token);

    public Task<RuntimeRpcAppProviderResponse> GetAppRpcProviderAsync(
        RuntimeRpcAppProviderRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppProviderRequest, RuntimeRpcAppProviderResponse>(
            "rpc/app/provider",
            request,
            RuntimeProtocolFeatures.AppWebRpcV1,
            token);

    public Task<RuntimeRpcAppInvokeResponse> InvokeAppRpcAsync(
        RuntimeRpcAppInvokeRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRpcAppInvokeRequest, RuntimeRpcAppInvokeResponse>(
            "rpc/app/invoke",
            request,
            RuntimeProtocolFeatures.AppWebRpcV1,
            token);

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
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, RpcStreamJsonOptions)));
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { RuntimeProtocolFeatures.AppWebRpcV1 });
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<RuntimeRpcAppContentRegisterResponse>(response, token)
            .ConfigureAwait(false);
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
            Content = JsonContent.Create(payload),
        };
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { RuntimeProtocolFeatures.AppWebRpcV1 });
        return request;
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
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: false);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (Encoding.UTF8.GetByteCount(line) > _transport.Policy.MaxStreamEventBytes)
            {
                throw new InvalidDataException("Runtime RPC stream frame exceeded the client parser limit.");
            }
            yield return JsonSerializer.Deserialize<TFrame>(line, RpcStreamJsonOptions)
                         ?? throw new InvalidDataException("Runtime RPC stream returned an empty frame.");
        }
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
