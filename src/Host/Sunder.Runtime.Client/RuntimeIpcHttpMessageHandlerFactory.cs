using System.IO.Pipes;
using System.Net.Sockets;

namespace Sunder.Runtime.Client;

public static class RuntimeIpcHttpMessageHandlerFactory
{
    public static HttpMessageHandler Create(RuntimeIpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.EnsureSupportedPlatform();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = (_, cancellationToken) => ConnectAsync(endpoint, cancellationToken),
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        RuntimeIpcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Kind == RuntimeIpcTransportKind.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                endpoint.Address,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(endpoint.Address),
                cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
