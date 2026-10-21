using System.Text.Json;

namespace Sunder.Runtime.Client;

public sealed class RuntimeHttpResponseReader
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };
    private readonly RuntimeClientPolicyOptions _policy;
    private readonly Func<RuntimeClientResourceLimits> _getResourceLimits;

    public RuntimeHttpResponseReader(RuntimeClientPolicyOptions policy)
        : this(policy, static () => RuntimeClientResourceLimits.Standard)
    {
    }

    internal RuntimeHttpResponseReader(
        RuntimeClientPolicyOptions policy,
        Func<RuntimeClientResourceLimits> getResourceLimits)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _getResourceLimits = getResourceLimits ?? throw new ArgumentNullException(nameof(getResourceLimits));
    }

    public async Task<T> ReadRequiredJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var limits = _getResourceLimits();
            var payload = await ReadBoundedAsync(
                response.Content,
                Math.Min(_policy.MaxJsonResponseBytes, limits.MaximumJsonEnvelopeBytes),
                cancellationToken);
            if (payload.Length > 0
                && JsonSerializer.Deserialize<T>(payload, limits.CreateJsonOptions()) is { } value)
            {
                return value;
            }
        }
        throw await CreateExceptionAsync(response, cancellationToken);
    }

    public async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var limits = _getResourceLimits();
        var payload = await ReadBoundedAsync(
            response.Content,
            Math.Min(_policy.MaxJsonResponseBytes, limits.MaximumJsonEnvelopeBytes),
            cancellationToken);
        return payload.Length == 0
            ? default
            : JsonSerializer.Deserialize<T>(payload, limits.CreateJsonOptions());
    }

    public Task<byte[]> ReadBinaryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => ReadBoundedAsync(response.Content, _policy.MaxBinaryResponseBytes, cancellationToken);

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
            var payload = await ReadBoundedAsync(response.Content, _policy.MaxErrorResponseBytes, cancellationToken);
            if (payload.Length > 0)
            {
                problem = JsonSerializer.Deserialize<RuntimeProblemDetails>(payload, ErrorJsonOptions);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }
        return CreateException(response, problem);
    }

    private static RuntimeClientException CreateException(HttpResponseMessage response, RuntimeProblemDetails? problem)
    {
        var title = problem?.Title ?? $"Runtime request failed with HTTP {(int)response.StatusCode}";
        var correlationId = problem?.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId)
            && response.Headers.TryGetValues("X-Correlation-ID", out var values))
        {
            correlationId = values.FirstOrDefault();
        }
        return new RuntimeClientException(
            response.StatusCode,
            title,
            problem?.Detail,
            problem?.Code,
            correlationId,
            innerException: null) { Details = problem?.Details?.Clone() };
    }

    internal async Task<byte[]> ReadRequiredPayloadAsync(
        HttpResponseMessage response,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }
        return await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken);
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
        }
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = maxBytes - destination.Length;
            var readLength = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (read == 0) return destination.ToArray();
            if (read > remaining)
            {
                throw new InvalidDataException($"Runtime response payload exceeds the {maxBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    // The policy heartbeat uses genuine synchronous I/O on its own thread. Do not
    // bridge to ReadAsStreamAsync/ReadAsync: those continuations need the pool.
    internal T ReadRequiredJson<T>(HttpResponseMessage response, CancellationToken token)
    {
        var limits = _getResourceLimits();
        var payload = ReadRequiredPayload(response,
            Math.Min(_policy.MaxJsonResponseBytes, limits.MaximumJsonEnvelopeBytes), token);
        return JsonSerializer.Deserialize<T>(payload, limits.CreateJsonOptions())
            ?? throw new InvalidDataException("Runtime did not return a JSON response.");
    }

    internal byte[] ReadRequiredPayload(HttpResponseMessage response, long maximumBytes, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return ReadBounded(response.Content, maximumBytes, token);
        RuntimeProblemDetails? problem = null;
        try
        {
            var payload = ReadBounded(response.Content, _policy.MaxErrorResponseBytes, token);
            if (payload.Length > 0) problem = JsonSerializer.Deserialize<RuntimeProblemDetails>(payload, ErrorJsonOptions);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { }
        throw CreateException(response, problem);
    }

    private static byte[] ReadBounded(HttpContent content, long maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException($"Runtime response payload exceeds the {maximumBytes} byte limit.");
        using var source = content.ReadAsStream(token);
        // A stalled body must remain cancellable, including after response headers.
        using var cancel = token.UnsafeRegister(static state => ((Stream)state!).Dispose(), source);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = maximumBytes - destination.Length;
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining + 1));
            if (read == 0) return destination.ToArray();
            if (read > remaining)
                throw new InvalidDataException($"Runtime response payload exceeds the {maximumBytes} byte limit.");
            destination.Write(buffer, 0, read);
        }
    }

    private sealed record RuntimeProblemDetails(
        string? Title,
        string? Detail,
        string? Code,
        string? CorrelationId,
        JsonElement? Details);
}
