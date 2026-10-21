using System.Diagnostics;
using System.Net.Sockets;

namespace Sunder.Runtime.Client;

internal enum RuntimePolicyTransportPhase
{
    Idle, SelectConnection, ValidateConnection, CreateSocket, Connect, WaitForConnect,
    ConfigureSocket, ConnectPipe, WriteHeaders, WriteBody, FlushRequest, ReadHeaders, ReadBody,
}

internal enum RuntimePolicyTransportKind { Unselected, Loopback, UnixSocket, NamedPipe }

// Local diagnostic evidence, never policy authority or an error-response payload.
// Keep the original exception and stack, and retain no URL, token, pipe path,
// request/response bytes or exception message in the attached record.
internal sealed record RuntimePolicyTransportFailure(RuntimePolicyTransportPhase Phase,
    RuntimePolicyTransportKind Transport, SocketError? SocketError, int? NativeError, double? ElapsedMilliseconds)
{
    private static readonly object Key = new();

    internal static void Capture(Exception error, RuntimePolicyTransportPhase phase, RuntimePolicyTransportKind transport, long started)
    {
        var socket = FindSocket(error);
        error.Data[Key] = new RuntimePolicyTransportFailure(phase, transport, socket?.SocketErrorCode,
            socket?.NativeErrorCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal static RuntimePolicyTransportFailure? From(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.InnerException)
            if (current.Data[Key] is RuntimePolicyTransportFailure failure) return failure;
        return FindSocket(error) is { } socket
            ? new(RuntimePolicyTransportPhase.Idle, RuntimePolicyTransportKind.Unselected, socket.SocketErrorCode, socket.NativeErrorCode, null) : null;
    }

    private static SocketException? FindSocket(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.InnerException)
            if (current is SocketException socket) return socket;
        return null;
    }
}
