using System.Text.Json;

namespace Sunder.Runtime.Client;

public sealed class RuntimeHttpResponseReader(RuntimeClientPolicyOptions policy)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T> ReadRequiredJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        bool acceptErrorPayload = false)
    {
        if (response.IsSuccessStatusCode || acceptErrorPayload)
        {
            try
            {
                var payload = await ReadBoundedAsync(response.Content, policy.MaxJsonResponseBytes, cancellationToken);
                if (payload.Length > 0 && JsonSerializer.Deserialize<T>(payload, JsonOptions) is { } value) return value;
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
            }
        }
        throw await CreateExceptionAsync(response, cancellationToken);
    }

    public async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await ReadBoundedAsync(response.Content, policy.MaxJsonResponseBytes, cancellationToken);
        return payload.Length == 0 ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public Task<byte[]> ReadBinaryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => ReadBoundedAsync(response.Content, policy.MaxBinaryResponseBytes, cancellationToken);

    public Task<byte[]> ReadBinaryAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
        => ReadBoundedAsync(response.Content, maxBytes, cancellationToken);

    public async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw await CreateExceptionAsync(response, cancellationToken);
    }

    private async Task<RuntimeClientException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        RuntimeProblemDetails? problem = null;
        try
        {
            var payload = await ReadBoundedAsync(response.Content, policy.MaxErrorResponseBytes, cancellationToken);
            if (payload.Length > 0) problem = JsonSerializer.Deserialize<RuntimeProblemDetails>(payload, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }
        var title = problem?.Title ?? $"Runtime request failed with HTTP {(int)response.StatusCode}";
        return new RuntimeClientException(response.StatusCode, title, problem?.Detail, problem?.Code);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
        }
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > maxBytes)
            {
                throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    private sealed record RuntimeProblemDetails(string? Title, string? Detail, string? Code);
}
