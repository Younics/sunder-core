using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class RuntimePolicyTransportDiagnosticsTests
{
    [UnixPolicyTransportTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidBufferedReplySurvivesPeerCloseBetweenHeadersAndBody(bool chunked)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "s");
        using var headersRead = new ManualResetEventSlim();
        using var peerClosed = new ManualResetEventSlim();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Thread? server = null;
        Exception? serverError = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var connection = new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "unused-reader-secret")
            { IpcEndpoint = new(RuntimeIpcTransportKind.UnixSocket, path) };
            using var handler = new RuntimePolicyHttpMessageHandler(() => connection, requireIpc: true);
            var started = Stopwatch.GetTimestamp();
            using var stream = handler.Connect(connection, started, default);
            using var peer = listener.Accept();
            var body = Encoding.ASCII.GetBytes(new string('x', 256));
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n" +
                (chunked ? "Transfer-Encoding: chunked\r\n\r\n" : $"Content-Length: {body.Length}\r\n\r\n"));
            server = new Thread(() =>
            {
                try
                {
                    using var output = new NetworkStream(peer);
                    output.Write(header);
                    if (!headersRead.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("No header read.");
                    if (chunked) output.Write(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"));
                    output.Write(body);
                    if (chunked) output.Write("\r\n0\r\n\r\n"u8);
                    // Ensure the remaining read budget changes, and the graceful
                    // close is visible before the client consumes buffered body.
                    Thread.Sleep(8);
                }
                catch (Exception error) { serverError = error; }
                finally { peer.Dispose(); peerClosed.Set(); }
            }) { IsBackground = true };
            server.Start();
            using var split = new HeaderBoundaryStream(stream, header.Length, headersRead, peerClosed);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.RuntimeUrl, "api/handshake"));
            using var response = RuntimePolicyHttpMessageHandler.ReadResponse(split, request, body.Length, started, default);
            using var result = response.Content.ReadAsStream();
            Assert.Equal(body, ReadAll(result));
        }
        finally
        {
            headersRead.Set();
            if (server is not null) Assert.True(server.Join(TimeSpan.FromSeconds(3)), "Owned peer did not stop.");
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
        Assert.Null(serverError);

        static byte[] ReadAll(Stream source) { using var bytes = new MemoryStream(); source.CopyTo(bytes); return bytes.ToArray(); }
    }

    [Fact]
    public void RealConnectFailureRetainsNativeOrDeadlineEvidenceWithoutLeakingCredentials()
    {
        // Bound but not listening: the port is reserved, with no accepting server.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)reserved.LocalEndPoint!;
        var connection = new RuntimeConnectionInfo(new Uri($"http://127.0.0.1:{endpoint.Port}/"), "control-secret");
        using var handler = new RuntimePolicyHttpMessageHandler(() => connection);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.RuntimeUrl, "api/handshake"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        var error = Record.Exception(() => client.Send(request));
        Assert.NotNull(error);
        // Some kernels reject this reserved non-listening endpoint immediately;
        // others leave the connect pending. Keep that distinction, never invent
        // a SocketException/code when the local deadline is the actual failure.
        Assert.True(error is SocketException or TimeoutException, error.ToString());
        var failure = Assert.IsType<RuntimePolicyTransportFailure>(RuntimePolicyTransportFailure.From(error));
        Assert.Contains(failure.Phase, new[] { RuntimePolicyTransportPhase.Connect, RuntimePolicyTransportPhase.WaitForConnect });
        Assert.Equal(RuntimePolicyTransportKind.Loopback, failure.Transport);
        Assert.Equal((error as SocketException)?.SocketErrorCode, failure.SocketError);
        Assert.Equal((error as SocketException)?.NativeErrorCode, failure.NativeError);
        Assert.InRange(failure.ElapsedMilliseconds!.Value, 0, 2500);
        Assert.DoesNotContain("control-secret", JsonSerializer.Serialize(failure));
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(failure));
        Assert.Equal("Idle", handler.DiagnosticPhase);
    }

    [Fact]
    public void WrappedReadFailureDoesNotLoseSocketEvidenceOrLogItsPrivateMessage()
    {
        var socket = new SocketException((int)SocketError.ConnectionReset);
        var error = new IOException("Bearer private-response-secret http://private/socket/path", socket);
        RuntimePolicyTransportFailure.Capture(error, RuntimePolicyTransportPhase.ReadBody, RuntimePolicyTransportKind.UnixSocket, Stopwatch.GetTimestamp());
        var failure = Assert.IsType<RuntimePolicyTransportFailure>(RuntimePolicyTransportFailure.From(new IOException("outer-secret", error)));
        Assert.Equal(SocketError.ConnectionReset, failure.SocketError);
        Assert.Equal(socket.NativeErrorCode, failure.NativeError);
        Assert.Equal(RuntimePolicyTransportPhase.ReadBody, failure.Phase);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(failure));
        Assert.Same(socket, error.InnerException);
    }

    [Fact]
    public async Task ActivePhaseCanBeObservedWhileConnectionDiscoveryIsStillBlocked()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var handler = new RuntimePolicyHttpMessageHandler(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned discovery was not released.");
            return null;
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/api/handshake");
        var send = Task.Run(() => Record.Exception(() => client.Send(request)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal("SelectConnection", handler.DiagnosticPhase);
        }
        finally { release.Set(); }
        Assert.IsType<InvalidOperationException>(await send.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Idle", handler.DiagnosticPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingSocketReadAndWriteRemainBoundedByTheAbsoluteExchangeDeadline(bool write)
    {
        using var pair = new ConnectedSockets();
        var started = Stopwatch.GetTimestamp();
        using var stream = new RuntimePolicySocketStream(pair.Client, started, default);
        Assert.False(stream.CanTimeout); // No mutable kernel timeout options.
        Assert.Throws<TimeoutException>(() =>
        {
            if (write) stream.Write(new byte[512 * 1024]); // Peer deliberately does not drain its small buffer.
            else Assert.Equal(1, stream.Read(new byte[1])); // Peer deliberately sends nothing.
        });
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1500, 3000);
    }

    [Fact]
    public void NonblockingSocketStreamPreservesExactByteOrderAcrossPartialWrites()
    {
        using var pair = new ConnectedSockets();
        var payload = Enumerable.Range(0, 32 * 1024).Select(index => (byte)(index % 251)).ToArray();
        Assert.True(payload.Length > pair.Client.SendBufferSize);
        var received = new byte[payload.Length];
        Exception? peerError = null;
        var reader = new Thread(() =>
        {
            try
            {
                pair.Peer.ReceiveTimeout = 3000;
                var offset = 0;
                while (offset < received.Length)
                {
                    var count = pair.Peer.Receive(received.AsSpan(offset, Math.Min(1024, received.Length - offset)));
                    if (count == 0) throw new EndOfStreamException("Incomplete request.");
                    offset += count;
                }
            }
            catch (Exception error) { peerError = error; }
        }) { IsBackground = true };
        reader.Start();
        try
        {
            using var stream = new RuntimePolicySocketStream(pair.Client, Stopwatch.GetTimestamp(), default);
            stream.Write(payload);
        }
        finally { Assert.True(reader.Join(TimeSpan.FromSeconds(4)), "Owned receiver did not stop."); }
        Assert.Null(peerError);
        Assert.Equal(payload, received);
    }

    private sealed class ConnectedSockets : IDisposable
    {
        internal Socket Client { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        internal Socket Peer { get; }
        internal ConnectedSockets()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            Client.Connect(listener.LocalEndPoint!);
            Peer = listener.Accept();
            Client.NoDelay = true;
            Client.SendBufferSize = 1024;
            Peer.ReceiveBufferSize = 1024;
            Client.Blocking = false;
        }
        public void Dispose() { Client.Dispose(); Peer.Dispose(); }
    }

    private sealed class HeaderBoundaryStream(Stream inner, int headerBytes, ManualResetEventSlim headersRead,
        ManualResetEventSlim peerClosed) : Stream
    {
        private int _remaining = headerBytes;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override bool CanTimeout => inner.CanTimeout;
        public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(_remaining > 0 ? buffer[..Math.Min(buffer.Length, _remaining)] : buffer);
            if (_remaining > 0)
            {
                _remaining -= count;
                if (_remaining == 0)
                {
                    headersRead.Set();
                    if (!peerClosed.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Peer did not close.");
                }
            }
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class UnixPolicyTransportTheoryAttribute : TheoryAttribute
{
    public UnixPolicyTransportTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix platform.";
    }
}
