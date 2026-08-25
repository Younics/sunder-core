using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sunder.Core.TestSupport;

public sealed class LoopbackStreamingHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<LoopbackHttpResponse> _responses;
    private readonly ConcurrentQueue<LoopbackHttpRequest> _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;

    private LoopbackStreamingHttpServer(
        TcpListener listener,
        IEnumerable<LoopbackHttpResponse> responses)
    {
        _listener = listener;
        _responses = new ConcurrentQueue<LoopbackHttpResponse>(responses);
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}/", UriKind.Absolute);
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri BaseAddress { get; }

    public IReadOnlyList<LoopbackHttpRequest> Requests => _requests.ToArray();

    public static LoopbackStreamingHttpServer Start(params LoopbackHttpResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackStreamingHttpServer(listener, responses);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
        }
        await Task.WhenAll(_connections.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            var connection = HandleConnectionAsync(client);
            _connections.Add(connection);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            var response = _responses.TryDequeue(out var queued)
                ? queued
                : LoopbackHttpResponse.Text("No response was queued.", statusCode: 500);
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _lifetime.Token).ConfigureAwait(false);
                _requests.Enqueue(request);
                response.SignalRequest(request);

                var declaredLength = response.DeclaredContentLength
                                     ?? response.Chunks.Sum(chunk => (long)chunk.Content.Length);
                var headers = new StringBuilder()
                    .Append("HTTP/1.1 ")
                    .Append(response.StatusCode)
                    .Append(' ')
                    .Append(ReasonPhrase(response.StatusCode))
                    .Append("\r\nConnection: close\r\nContent-Length: ")
                    .Append(declaredLength)
                    .Append("\r\n");
                foreach (var (name, value) in response.Headers)
                {
                    headers.Append(name).Append(": ").Append(value).Append("\r\n");
                }
                headers.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                response.SignalHeadersSent();

                foreach (var chunk in response.Chunks)
                {
                    if (chunk.BeforeWrite is not null)
                    {
                        await chunk.BeforeWrite.EnterAndWaitAsync(_lifetime.Token).ConfigureAwait(false);
                    }
                    await stream.WriteAsync(chunk.Content, _lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                    chunk.SignalWritten();
                }
                response.SignalCompleted();
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                response.SignalDisconnected(exception);
            }
        }
    }

    private static async Task<LoopbackHttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1];
        while (memory.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("Client disconnected before sending complete HTTP headers.");
            memory.WriteByte(buffer[0]);
            if (memory.Length >= 4)
            {
                var bytes = memory.GetBuffer();
                var length = (int)memory.Length;
                if (bytes[length - 4] == '\r'
                    && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r'
                    && bytes[length - 1] == '\n')
                {
                    var lines = Encoding.ASCII.GetString(bytes, 0, length - 4).Split("\r\n", StringSplitOptions.None);
                    var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (requestLine.Length != 3) throw new IOException("Client sent a malformed HTTP request line.");
                    var headers = lines.Skip(1)
                        .Select(line => line.Split(':', 2))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    return new LoopbackHttpRequest(requestLine[0], requestLine[1], headers);
                }
            }
        }
        throw new IOException("Client HTTP headers exceeded the loopback fixture limit.");
    }

    private static string ReasonPhrase(int statusCode)
        => statusCode switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "Response",
        };
}

public sealed class LoopbackHttpResponse
{
    private readonly TaskCompletionSource<LoopbackHttpRequest> _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _headersSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LoopbackHttpResponse(
        int statusCode,
        IReadOnlyList<LoopbackHttpChunk> chunks,
        IReadOnlyDictionary<string, string>? headers = null,
        long? declaredContentLength = null)
    {
        StatusCode = statusCode;
        Chunks = chunks;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DeclaredContentLength = declaredContentLength;
    }

    public int StatusCode { get; }

    public IReadOnlyList<LoopbackHttpChunk> Chunks { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public long? DeclaredContentLength { get; }

    public Task<LoopbackHttpRequest> RequestReceived => _requestReceived.Task;

    public Task HeadersSent => _headersSent.Task;

    public Task Completed => _completed.Task;

    public Task<Exception> ClientDisconnected => _disconnected.Task;

    public static LoopbackHttpResponse Text(string content, int statusCode = 200, string mediaType = "text/plain")
        => new(
            statusCode,
            [new LoopbackHttpChunk(Encoding.UTF8.GetBytes(content))],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = mediaType,
            });

    public static LoopbackHttpResponse Json(string content, int statusCode = 200)
        => Text(content, statusCode, "application/json");

    internal void SignalRequest(LoopbackHttpRequest request) => _requestReceived.TrySetResult(request);

    internal void SignalHeadersSent() => _headersSent.TrySetResult();

    internal void SignalCompleted() => _completed.TrySetResult();

    internal void SignalDisconnected(Exception exception) => _disconnected.TrySetResult(exception);
}

public sealed class LoopbackHttpChunk
{
    private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LoopbackHttpChunk(byte[] content, ObservableGate? beforeWrite = null)
    {
        Content = content;
        BeforeWrite = beforeWrite;
    }

    public byte[] Content { get; }

    public ObservableGate? BeforeWrite { get; }

    public Task Written => _written.Task;

    internal void SignalWritten() => _written.TrySetResult();
}

public sealed record LoopbackHttpRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers);
