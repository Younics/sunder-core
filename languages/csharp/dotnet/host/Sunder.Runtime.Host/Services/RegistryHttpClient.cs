using System.Net;
using System.Text.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryHttpClient(
    IHttpClientFactory httpClientFactory,
    RuntimeTransportPolicyOptions policy,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(policy.RegistryRequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await httpClientFactory.CreateClient("registry").SendAsync(request, completionOption, linked.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Registry request exceeded the {policy.RegistryRequestTimeout} timeout.", exception);
        }
    }

    public async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await ReadBoundedAsync(response.Content, policy.MaxRegistryJsonBytes, cancellationToken);
        return payload.Length == 0 ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await ReadBoundedAsync(response.Content, policy.MaxRegistryErrorBytes, cancellationToken);
            if (payload.Length > 0)
            {
                var problem = JsonSerializer.Deserialize<RegistryProblemDetails>(payload, JsonOptions);
                if (!string.IsNullOrWhiteSpace(problem?.Detail)) return problem.Detail;
                if (!string.IsNullOrWhiteSpace(problem?.Title)) return problem.Title;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "Registry rejected the request.",
            HttpStatusCode.Unauthorized => "Registry sign-in is required.",
            HttpStatusCode.Forbidden => "Registry operation is forbidden.",
            HttpStatusCode.NotFound => "Registry resource was not found.",
            HttpStatusCode.Conflict => "Registry operation conflicted with existing state.",
            _ => $"Registry request failed with HTTP {(int)response.StatusCode}.",
        };
    }

    public async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(
            await ReadErrorAsync(response, cancellationToken),
            inner: null,
            response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"HTTP response payload exceeds the {maxBytes} byte limit.");
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
                throw new InvalidDataException($"HTTP response payload exceeds the {maxBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }
}
