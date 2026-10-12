using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal sealed class RuntimeGateway : IDisposable
{
    private const int RequestPreflightBufferBytes = 64 * 1024;
    private const string CorrelationHeader = "X-Correlation-ID";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    private readonly IRuntimeWorkerConnectionSource _connections;
    private readonly object _clientGate = new();
    private readonly HttpClient? _fixedClient;
    private readonly Func<RuntimeIpcEndpoint, HttpMessageHandler> _handlerFactory;
    private HttpClient? _workerClient;
    private long _workerEpoch;

    public RuntimeGateway(
        IRuntimeWorkerConnectionSource connections,
        HttpMessageHandler? handler = null,
        Func<RuntimeIpcEndpoint, HttpMessageHandler>? handlerFactory = null)
    {
        _connections = connections;
        _handlerFactory = handlerFactory ?? RuntimeIpcHttpMessageHandlerFactory.Create;
        _fixedClient = handler is null ? null : CreateClient(handler);
    }

    public async Task ForwardAsync(HttpContext context)
    {
        if (!await IsWithinRequestBodyLimitAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var connection = _connections.GetWorkerConnection();
        if (connection is null)
        {
            await WriteUnavailableAsync(context).ConfigureAwait(false);
            return;
        }

        var relativePath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        var target = new Uri(
            Sunder.Runtime.LocalState.RuntimeConnectionInfo.Normalize(connection.ConnectionInfo.RuntimeUrl),
            relativePath.TrimStart('/'));
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (HasRequestBody(context.Request))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        CopyRequestHeaders(context.Request, request);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            connection.ConnectionInfo.BearerToken);

        try
        {
            var client = GetClient(connection);
            if (_connections.GetWorkerConnection()?.WorkerEpoch != connection.WorkerEpoch)
            {
                await WriteUnavailableAsync(context).ConfigureAwait(false);
                return;
            }
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response);
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            await WriteUnavailableAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            await WriteUnavailableAsync(context).ConfigureAwait(false);
        }
    }

    private static bool HasRequestBody(HttpRequest request)
        => request.ContentLength is > 0
           || request.Headers.ContainsKey("Transfer-Encoding")
           || HttpMethods.IsPost(request.Method)
           || HttpMethods.IsPut(request.Method)
           || HttpMethods.IsPatch(request.Method);

    private static async Task<bool> IsWithinRequestBodyLimitAsync(HttpContext context)
    {
        var request = context.Request;
        var maximumBytes = GetMaximumRequestBodyBytes(request);
        if (!HasRequestBody(request))
        {
            return true;
        }
        if (request.ContentLength is { } contentLength && contentLength <= maximumBytes)
        {
            return true;
        }
        var declaredOverflow = request.ContentLength > maximumBytes;

        // Read one sentinel byte locally so unknown-length overflow never reaches worker IPC.
        var maxRequestBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySize is { IsReadOnly: false })
        {
            maxRequestBodySize.MaxRequestBodySize = maximumBytes + 1;
        }
        request.EnableBuffering(
            RequestPreflightBufferBytes,
            maximumBytes + 1);
        var buffer = new byte[RequestPreflightBufferBytes];
        long observedBytes = 0;
        try
        {
            while (true)
            {
                var remainingThroughOverflow = maximumBytes + 1 - observedBytes;
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingThroughOverflow)),
                    context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    if (declaredOverflow)
                    {
                        await WriteUploadLimitAsync(context, maximumBytes).ConfigureAwait(false);
                        return false;
                    }
                    request.Body.Position = 0;
                    return true;
                }

                observedBytes += read;
                if (observedBytes > maximumBytes)
                {
                    await WriteUploadLimitAsync(context, maximumBytes).ConfigureAwait(false);
                    return false;
                }
            }
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteUploadLimitAsync(context, maximumBytes).ConfigureAwait(false);
            return false;
        }
    }

    internal static long GetMaximumRequestBodyBytes(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes;
        }

        var path = request.Path.Value;
        if (path is { Length: > 1 } && path[^1] == '/')
        {
            path = path[..^1];
        }
        if (string.Equals(path, "/api/v1/uploads/packages", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeRequestSizeLimits.PackageUploadMaxRequestBodyBytes;
        }
        if (string.Equals(path, "/api/v1/uploads/stacks", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeRequestSizeLimits.StackUploadMaxRequestBodyBytes;
        }
        if (string.Equals(path, "/api/v1/uploads/stack-media", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeRequestSizeLimits.StackMediaUploadMaxRequestBodyBytes;
        }
        return RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes;
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        var connectionHeaders = ParseConnectionHeaders(source.Headers["Connection"]);
        foreach (var header in source.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)
                || connectionHeaders.Contains(header.Key)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Forwarded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Sunder-Client-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Sunder-Host-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Sunder-Worker-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                destination.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        var connectionHeaders = source.Headers.TryGetValues("Connection", out var values)
            ? ParseConnectionHeaders(values)
            : [];
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key)
                && !connectionHeaders.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }

    private static HashSet<string> ParseConnectionHeaders(IEnumerable<string> values)
        => values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
        }
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "1";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = "about:blank",
                title = "Runtime worker unavailable",
                status = StatusCodes.Status503ServiceUnavailable,
                detail = "The Sunder Host is reachable, but its Runtime worker is not ready.",
                code = "host.runtime-unavailable",
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteUploadLimitAsync(HttpContext context, long maximumBytes)
    {
        var correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers[CorrelationHeader] = correlationId;
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = "https://sunder.dev/problems/runtime.v1.upload-limit",
                title = "Upload limit exceeded",
                status = StatusCodes.Status413PayloadTooLarge,
                detail = $"Request body too large. The max request body size is {maximumBytes} bytes.",
                code = "runtime.v1.upload-limit",
                correlationId,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            CancellationToken.None).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(supplied)
            && supplied.Length <= 128
            && supplied.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return supplied;
        }

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }

    private HttpClient GetClient(RuntimeWorkerConnection connection)
    {
        if (_fixedClient is not null)
        {
            return _fixedClient;
        }
        lock (_clientGate)
        {
            if (_workerClient is not null && _workerEpoch == connection.WorkerEpoch)
            {
                return _workerClient;
            }
            if (connection.WorkerEpoch < _workerEpoch)
            {
                throw new IOException("Runtime worker epoch is stale.");
            }
            _workerClient?.Dispose();
            _workerClient = CreateClient(_handlerFactory(connection.Endpoint));
            _workerEpoch = connection.WorkerEpoch;
            return _workerClient;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public void Dispose()
    {
        lock (_clientGate)
        {
            _workerClient?.Dispose();
            _fixedClient?.Dispose();
        }
    }
}
