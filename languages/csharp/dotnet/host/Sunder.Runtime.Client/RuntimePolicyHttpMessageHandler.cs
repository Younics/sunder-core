using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Sunder.Runtime.Client;

// The two fixed local policy operations cannot use HttpClient's connection pool:
// even Send() queues new connections on the shared ThreadPool. Use bounded,
// synchronous HTTP/1.1 over a fresh local connection, never redirects/DNS/proxies.
internal sealed class RuntimePolicyHttpMessageHandler(Func<RuntimeConnectionInfo?> connection, bool requireIpc = false) : HttpMessageHandler
{
    internal static readonly HttpRequestOptionsKey<long> MaximumResponseBytes = new("Sunder.Policy.MaximumResponseBytes");
    internal static readonly HttpRequestOptionsKey<long> MaximumErrorBytes = new("Sunder.Policy.MaximumErrorBytes");
    internal static readonly HttpRequestOptionsKey<long> ExchangeStarted = new("Sunder.Policy.ExchangeStarted");
    private const int MaximumHeaderBytes = 32 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private int _phase;
    private RuntimePolicyTransportKind _transportKind;
    internal string DiagnosticPhase => ((RuntimePolicyTransportPhase)Volatile.Read(ref _phase)).ToString();
    private void SetPhase(RuntimePolicyTransportPhase phase) => Volatile.Write(ref _phase, (int)phase);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
    {
        var started = request.Options.TryGetValue(ExchangeStarted, out var exchangeStarted) ? exchangeStarted : Stopwatch.GetTimestamp();
        _transportKind = RuntimePolicyTransportKind.Unselected;
        try { return SendCore(request, started, token); }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException or OperationCanceledException)
        {
            RuntimePolicyTransportFailure.Capture(error, (RuntimePolicyTransportPhase)Volatile.Read(ref _phase), _transportKind, started);
            throw;
        }
        finally { SetPhase(RuntimePolicyTransportPhase.Idle); }
    }

    private HttpResponseMessage SendCore(HttpRequestMessage request, long started, CancellationToken token)
    {
        _ = RemainingMilliseconds(started, token);
        SetPhase(RuntimePolicyTransportPhase.SelectConnection);
        var selected = connection();
        SetPhase(RuntimePolicyTransportPhase.ValidateConnection);
        _transportKind = selected?.IpcEndpoint?.Kind switch
        {
            RuntimeIpcTransportKind.NamedPipe => RuntimePolicyTransportKind.NamedPipe,
            RuntimeIpcTransportKind.UnixSocket => RuntimePolicyTransportKind.UnixSocket,
            _ => selected is null ? RuntimePolicyTransportKind.Unselected : RuntimePolicyTransportKind.Loopback,
        };
        if (selected is null || requireIpc && selected.IpcEndpoint is null || request.RequestUri is not { } uri
            || !selected.CanSendTo(uri) || request.Headers.Authorization?.Scheme != "Bearer"
            || request.Headers.Authorization.Parameter != selected.BearerToken
            || selected.BearerToken.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("The authenticated local policy connection changed.");
        using var stream = Connect(selected, started, token);
        using var cancel = token.UnsafeRegister(static value => ((Stream)value!).Dispose(), stream);
        var reader = new WireReader(stream, started, token);
        var length = request.Content?.Headers.ContentLength ?? 0;
        if (length < 0 || length > 16 * 1024) throw new InvalidDataException("Unbounded policy request.");
        var header = Encoding.ASCII.GetBytes($"{request.Method} {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\nAuthorization: Bearer {selected.BearerToken}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
        SetPhase(RuntimePolicyTransportPhase.WriteHeaders);
        reader.CheckDeadline();
        if (stream.CanTimeout) stream.WriteTimeout = RemainingMilliseconds(started, token);
        stream.Write(header);
        if (request.Content is { } content)
        {
            SetPhase(RuntimePolicyTransportPhase.WriteBody);
            using var body = content.ReadAsStream(token);
            var bytes = new byte[checked((int)length)];
            body.ReadExactly(bytes);
            reader.CheckDeadline();
            if (stream.CanTimeout) stream.WriteTimeout = RemainingMilliseconds(started, token);
            stream.Write(bytes);
        }
        SetPhase(RuntimePolicyTransportPhase.FlushRequest);
        stream.Flush();
        var maximum = request.Options.TryGetValue(MaximumResponseBytes, out var configured)
            ? Math.Min(configured, RuntimeClientResourceLimits.MaximumProtocolEnvelopeBytes)
            : RuntimeClientResourceLimits.MaximumProtocolEnvelopeBytes;
        return ReadResponse(stream, request, maximum, started, token, SetPhase);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => throw new NotSupportedException("The dedicated policy channel requires synchronous I/O.");

    internal static int RemainingMilliseconds(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = Deadline - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Local policy exchange exceeded its deadline.");
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
    }

    internal Stream Connect(RuntimeConnectionInfo selected, long started, CancellationToken token)
    {
        var remaining = RemainingMilliseconds(started, token);
        var endpoint = selected.IpcEndpoint;
        endpoint?.EnsureSupportedPlatform();
        if (endpoint?.Kind == RuntimeIpcTransportKind.NamedPipe)
        {
            SetPhase(RuntimePolicyTransportPhase.ConnectPipe);
            var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut,
                PipeOptions.None, TokenImpersonationLevel.Identification);
            try { pipe.Connect(remaining); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        EndPoint address;
        if (endpoint is not null) address = new UnixDomainSocketEndPoint(endpoint.Address);
        else
        {
            var uri = selected.RuntimeUrl;
            var host = uri.IdnHost;
            var ip = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
                : IPAddress.TryParse(host, out var parsed) ? parsed : null;
            if (uri.Scheme != "http" || ip is null || !IPAddress.IsLoopback(ip))
                throw new InvalidOperationException("Policy supervision requires a local IPC endpoint or literal HTTP loopback address.");
            address = new IPEndPoint(ip, uri.Port);
        }
        SetPhase(RuntimePolicyTransportPhase.CreateSocket);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cancel = token.UnsafeRegister(static value => ((Socket)value!).Dispose(), socket);
            socket.Blocking = false;
            SetPhase(RuntimePolicyTransportPhase.Connect);
            try { socket.Connect(address); }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
            {
                SetPhase(RuntimePolicyTransportPhase.WaitForConnect);
                if (!socket.Poll(checked(RemainingMilliseconds(started, token) * 1000), SelectMode.SelectWrite)) throw new TimeoutException("Local policy connection timed out.");
                var code = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                if (code != 0) throw new SocketException(code);
            }
            SetPhase(RuntimePolicyTransportPhase.ConfigureSocket);
            if (!socket.Connected) throw new IOException("Local policy connection did not complete.");
            return new RuntimePolicySocketStream(socket, started, token);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static HttpResponseMessage ReadResponse(Stream stream, HttpRequestMessage request, long maximum,
        long started, CancellationToken token, Action<RuntimePolicyTransportPhase>? phase = null)
    {
        phase?.Invoke(RuntimePolicyTransportPhase.ReadHeaders);
        var reader = new WireReader(stream, started, token);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        var status = reader.Line();
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || parts[0] is not ("HTTP/1.1" or "HTTP/1.0") || parts[1].Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is < 200 or > 599)
            throw new InvalidDataException("Invalid local policy HTTP status.");
        if (code >= 300 && request.Options.TryGetValue(MaximumErrorBytes, out var maximumError))
            maximum = Math.Min(maximum, maximumError);
        long? length = null;
        string? transfer = null;
        var headers = new List<KeyValuePair<string, string>>();
        for (var count = 0; ; count++)
        {
            var line = reader.Line();
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (count >= 64 || colon <= 0)
                throw new InvalidDataException("Invalid local policy HTTP headers.");
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)))
                throw new InvalidDataException("Invalid local policy HTTP header name.");
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length is not null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    throw new InvalidDataException("Invalid local policy HTTP content length.");
                length = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (transfer is not null || !value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid local policy HTTP transfer encoding.");
                transfer = value;
            }
            else if (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) && value != "identity")
                throw new InvalidDataException("Compressed policy responses are not supported.");
            headers.Add(new(name, value));
        }
        if (length is not null && transfer is not null || length > maximum)
            throw new InvalidDataException("Invalid or oversized local policy HTTP framing.");
        using var body = new MemoryStream();
        phase?.Invoke(RuntimePolicyTransportPhase.ReadBody);
        if (transfer is not null)
        {
            while (true)
            {
                var chunk = reader.Line();
                var digits = chunk.Split(';', 2)[0];
                if (digits.Length == 0 || !long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
                    || size < 0 || size > maximum - body.Length)
                    throw new InvalidDataException("Invalid or oversized local policy HTTP chunk.");
                if (size == 0)
                {
                    // Trailers cannot supply authentication, framing or policy data.
                    for (var count = 0; reader.Line().Length != 0; count++)
                        if (count >= 64) throw new InvalidDataException("Too many local policy HTTP trailers.");
                    break;
                }
                reader.Copy(body, size);
                if (reader.Line().Length != 0) throw new InvalidDataException("Invalid local policy HTTP chunk terminator.");
            }
        }
        else if (length is { } exact) reader.Copy(body, exact);
        else
        {
            // Connection: close is the only other framing accepted by this channel.
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = reader.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, maximum - body.Length + 1)));
                if (read == 0) break;
                if (read > maximum - body.Length) throw new InvalidDataException("Oversized local policy HTTP response.");
                body.Write(buffer, 0, read);
            }
        }
        reader.CheckDeadline();
        var response = new HttpResponseMessage((HttpStatusCode)code) { RequestMessage = request, Content = new ByteArrayContent(body.ToArray()) };
        foreach (var header in headers)
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        // The buffered bytes are already decoded from transfer framing.
        response.Headers.TransferEncoding.Clear();
        response.Content.Headers.ContentLength = body.Length;
        return response;
    }

    private sealed class WireReader(Stream stream, long started, CancellationToken token)
    {
        private int _headerBytes;
        private readonly byte[] _buffer = new byte[4096];
        private int _position;
        private int _count;
        private int _remainingMilliseconds;
        internal void CheckDeadline()
        {
            _remainingMilliseconds = RemainingMilliseconds(started, token);
        }
        internal int Read(Span<byte> buffer)
        {
            CheckDeadline();
            if (_position == _count)
            {
                _position = 0;
                _count = Receive(_buffer);
                if (_count == 0) return 0;
            }
            var count = Math.Min(buffer.Length, _count - _position);
            _buffer.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        private int Receive(Span<byte> buffer)
        {
            if (stream.CanTimeout) stream.ReadTimeout = _remainingMilliseconds;
            if (OperatingSystem.IsWindows() && stream is NamedPipeClientStream pipe)
            {
                uint available;
                while (true)
                {
                    if (!PeekNamedPipe(pipe.SafePipeHandle.DangerousGetHandle(), 0, 0, 0, out available, 0))
                    {
                        if (Marshal.GetLastPInvokeError() is 109 or 233) return 0; // broken/disconnected pipe: EOF
                        throw new IOException("Local policy pipe read failed.");
                    }
                    if (available > 0) break;
                    if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
                    CheckDeadline();
                }
                buffer = buffer[..(int)Math.Min(buffer.Length, available)];
            }
            return stream.Read(buffer);
        }
        internal string Line()
        {
            var bytes = new List<byte>();
            Span<byte> next = stackalloc byte[1];
            while (true)
            {
                if (++_headerBytes > MaximumHeaderBytes || Read(next) != 1) throw new InvalidDataException("Invalid or oversized local policy HTTP headers.");
                if (next[0] == '\r')
                {
                    if (++_headerBytes > MaximumHeaderBytes || Read(next) != 1 || next[0] != '\n')
                        throw new InvalidDataException("Invalid local policy HTTP line ending.");
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                if (next[0] is < 32 or > 126) throw new InvalidDataException("Invalid local policy HTTP header character.");
                bytes.Add(next[0]);
            }
        }
        internal void Copy(Stream destination, long length)
        {
            var buffer = new byte[16 * 1024];
            while (length > 0)
            {
                var read = Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, length)));
                if (read == 0) throw new EndOfStreamException("Incomplete local policy HTTP response.");
                destination.Write(buffer, 0, read); length -= read;
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(nint pipe, nint buffer, uint length, nint read, out uint available, nint remaining);
    }
}
