using System.Net;
using System.Net.Sockets;

namespace Sunder.App.Services;

internal static class ExternalImageNetworkGuard
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
        => await ConnectAsync(
            context.DnsEndPoint,
            ResolveAsync,
            ConnectSocketAsync,
            cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connectAsync,
        CancellationToken cancellationToken)
    {
        var addresses = await resolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (!AreAllAddressesPublic(addresses))
        {
            throw new HttpRequestException(
                $"External image host '{endpoint.Host}' resolved to a non-public address.");
        }

        Exception? lastException = null;
        foreach (var address in addresses.OrderBy(static address =>
                     address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await connectAsync(address, endpoint.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastException = exception;
            }
        }

        throw new HttpRequestException(
            $"Could not connect to external image host '{endpoint.Host}'.",
            lastException);
    }

    internal static bool IsAllowedExternalUri(Uri uri)
    {
        if (!HttpMediaUriValidator.IsValid(uri)
            || uri.Port is not (80 or 443))
        {
            return false;
        }

        return !IPAddress.TryParse(uri.DnsSafeHost, out var address) || IsPublicAddress(address);
    }

    internal static bool AreAllAddressesPublic(IReadOnlyCollection<IPAddress> addresses)
        => addresses.Count is > 0 and <= 16 && addresses.All(IsPublicAddress);

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            return first != 0
                   && first != 10
                   && first != 127
                   && first < 224
                   && !(first == 100 && second is >= 64 and <= 127)
                   && !(first == 169 && second == 254)
                   && !(first == 172 && second is >= 16 and <= 31)
                   && !(first == 192 && second == 168)
                   && !(first == 192 && second == 0 && bytes[2] is 0 or 2)
                   && !(first == 192 && second == 88 && bytes[2] == 99)
                   && !(first == 198 && second is 18 or 19)
                   && !(first == 198 && second == 51 && bytes[2] == 100)
                   && !(first == 203 && second == 0 && bytes[2] == 113);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
            || address.IsIPv6Teredo)
        {
            return false;
        }

        // Reject unique-local, translation, documentation, and transition ranges. These can
        // otherwise tunnel an apparently public IPv6 destination to a private IPv4 address.
        return (bytes[0] & 0xfe) != 0xfc
               && !(bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
               && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
               && !(bytes[0] == 0x20 && bytes[1] == 0x02)
               && !bytes[..12].All(static value => value == 0);
    }

    private static async Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host).WaitAsync(cancellationToken).ConfigureAwait(false);

    private static async ValueTask<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
