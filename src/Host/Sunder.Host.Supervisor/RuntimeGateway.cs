using System.Net.Http.Headers;
using System.Text.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal sealed class RuntimeGateway : IDisposable
{
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
                throw new RuntimeWorkerUnavailableException();
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

    private sealed class RuntimeWorkerUnavailableException : IOException;
}
