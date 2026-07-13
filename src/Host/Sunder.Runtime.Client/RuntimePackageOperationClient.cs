using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed class RuntimePackageOperationClient : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _getConnectionInfo;
    private readonly HttpClient _httpClient;
    private readonly RuntimeHttpResponseReader _responses;
    private readonly RuntimePackageOperationPolicyOptions _policy;

    public RuntimePackageOperationClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimePackageOperationPolicyOptions? policy = null)
    {
        _policy = policy ?? new RuntimePackageOperationPolicyOptions();
        var clientPolicy = new RuntimeClientPolicyOptions
        {
            RequestTimeout = _policy.RequestTimeout,
            StreamLifetimeTimeout = _policy.StreamLifetimeTimeout,
            MaxBinaryResponseBytes = _policy.MaxResponseBytes,
            MaxStreamEventBytes = _policy.MaxStreamRecordBytes,
        };
        _getConnectionInfo = getConnectionInfo ?? throw new ArgumentNullException(nameof(getConnectionInfo));
        _httpClient = new HttpClient(new RuntimeAuthenticatedHttpMessageHandler(getConnectionInfo, innerHandler, clientPolicy))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _responses = new RuntimeHttpResponseReader(clientPolicy);
    }

    public async Task<byte[]> InvokeAsync(
        string packageId,
        string operationId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > _policy.MaxRequestBytes)
        {
            throw new InvalidDataException("Package Runtime operation request exceeds the client limit.");
        }
        using var deadline = CreateDeadline(cancellationToken, _policy.RequestTimeout);
        using var content = new ReadOnlyMemoryContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _httpClient.PostAsync(
            CreateUri(packageId, operationId),
            content,
            deadline.Token).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, deadline.Token).ConfigureAwait(false);
        return await _responses.ReadBinaryAsync(response, _policy.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<byte[]> SubscribeAsync(
        string packageId,
        string streamId,
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (payload.Length > _policy.MaxRequestBytes)
        {
            throw new InvalidDataException("Package Runtime stream request exceeds the client limit.");
        }
        using var lifetime = CreateDeadline(cancellationToken, _policy.StreamLifetimeTimeout);
        var lifetimeToken = lifetime.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(packageId, "streams", streamId));
        request.Headers.ConnectionClose = true;
        request.Content = new ReadOnlyMemoryContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            lifetimeToken).ConfigureAwait(false);
        await _responses.EnsureSuccessAsync(response, lifetimeToken).ConfigureAwait(false);
        await using var responseStream = await response.Content.ReadAsStreamAsync(lifetimeToken).ConfigureAwait(false);
        var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        using var eventBuffer = new MemoryStream();
        var terminalFrameReceived = false;
        try
        {
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(
                       readBuffer.AsMemory(0, readBuffer.Length),
                       lifetimeToken).ConfigureAwait(false)) > 0)
            {
                var offset = 0;
                while (offset < bytesRead)
                {
                    var remaining = readBuffer.AsSpan(offset, bytesRead - offset);
                    var newlineOffset = remaining.IndexOf((byte)'\n');
                    var segmentLength = newlineOffset < 0 ? remaining.Length : newlineOffset;
                    if (eventBuffer.Length + segmentLength > _policy.MaxStreamRecordBytes)
                    {
                        throw new InvalidDataException("Package Runtime stream event exceeds the client limit.");
                    }
                    eventBuffer.Write(readBuffer, offset, segmentLength);
                    offset += segmentLength;
                    if (newlineOffset < 0)
                    {
                        continue;
                    }

                    offset++;
                    if (eventBuffer.Length == 0)
                    {
                        throw new InvalidDataException("Package Runtime stream returned an empty frame.");
                    }

                    using var frame = ParseFrame(eventBuffer.ToArray());
                    eventBuffer.SetLength(0);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("type", out var typeProperty)
                        || typeProperty.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("Package Runtime stream frame is missing its type.");
                    }
                    var type = typeProperty.GetString();
                    if (terminalFrameReceived)
                    {
                        throw new InvalidDataException("Package Runtime stream returned data after its terminal frame.");
                    }
                    if (string.Equals(type, PackageRuntimeStreamFrameTypes.Event, StringComparison.Ordinal))
                    {
                        if (!root.TryGetProperty("event", out var eventProperty)
                            || eventProperty.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        {
                            throw new InvalidDataException("Package Runtime event frame has no event payload.");
                        }
                        yield return Encoding.UTF8.GetBytes(eventProperty.GetRawText());
                        continue;
                    }
                    if (string.Equals(type, PackageRuntimeStreamFrameTypes.Completed, StringComparison.Ordinal))
                    {
                        terminalFrameReceived = true;
                        continue;
                    }
                    if (string.Equals(type, PackageRuntimeStreamFrameTypes.Error, StringComparison.Ordinal))
                    {
                        terminalFrameReceived = true;
                        var error = root.TryGetProperty("error", out var errorProperty)
                            ? errorProperty.Deserialize<PackageRuntimeStreamError>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            : null;
                        throw new RuntimePackageStreamException(
                            error?.Code ?? "runtime.package-stream.error",
                            error?.Message ?? "Package Runtime stream failed.");
                    }
                    throw new InvalidDataException($"Package Runtime stream returned unknown frame type '{type}'.");
                }
            }

            if (eventBuffer.Length > 0)
            {
                throw new InvalidDataException("Package Runtime stream ended with a partial JSON frame.");
            }
            if (!terminalFrameReceived)
            {
                throw new InvalidDataException("Package Runtime stream ended without a terminal frame.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static JsonDocument ParseFrame(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Package Runtime stream returned an invalid JSON frame.", exception);
        }
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private Uri CreateUri(string packageId, string operationId)
        => CreateUri(packageId, "operations", operationId);

    private Uri CreateUri(string packageId, string route, string contractId)
    {
        var connection = _getConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        return new Uri(
            RuntimeConnectionInfo.Normalize(connection.RuntimeUrl),
            $"api/v1/packages/{Uri.EscapeDataString(packageId)}/{route}/{Uri.EscapeDataString(contractId)}");
    }

    public void Dispose() => _httpClient.Dispose();
}
