using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AsyncImageLoader;
using AsyncImageLoader.Loaders;

namespace Sunder.App.Services;

internal sealed class SunderAsyncImageLoader(string cacheFolder)
    : DiskCachedWebImageLoader(CreateHttpClient(), disposeHttpClient: true, cacheFolder)
{
    private const string ImageAcceptHeader = "image/jpeg,image/png,*/*;q=0.1";
    private const string UserAgent = "Sunder.App/1.0";

    public static void Install()
    {
        var previous = ImageLoader.AsyncImageLoader;
        ImageLoader.AsyncImageLoader = new SunderAsyncImageLoader(GetCacheFolder());
        previous.Dispose();
    }

    protected override async Task<byte[]?> LoadDataFromExternalAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            request.Headers.Accept.ParseAdd(ImageAcceptHeader);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            var contentLength = response.Content.Headers.ContentLength;
            if (!response.IsSuccessStatusCode)
            {
                AppSessionLog.WriteError(
                    $"Async image request to '{DescribeUrl(url)}' returned HTTP {(int)response.StatusCode} {response.StatusCode} ({contentType}, {FormatContentLength(contentLength)}).",
                    visibleInDeveloperLog: false);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            AppSessionLog.WriteInfo(
                $"Async image loaded from '{DescribeUrl(url)}' ({contentType}, {bytes.Length} bytes).",
                visibleInDeveloperLog: false);
            return bytes;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(
                $"Async image request to '{DescribeUrl(url)}' failed: {ex.Message}",
                ex,
                visibleInDeveloperLog: false);
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectPreferIpv4Async,
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.Accept.ParseAdd(ImageAcceptHeader);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static async ValueTask<Stream> ConnectPreferIpv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var orderedAddresses = OrderAddressesPreferIpv4(addresses);
        Exception? lastException = null;
        foreach (var address in orderedAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket
                    .ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                AppSessionLog.WriteInfo(
                    $"Async image connection to '{context.DnsEndPoint.Host}' using {address.AddressFamily} {address}.",
                    visibleInDeveloperLog: false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"Could not connect to image host '{context.DnsEndPoint.Host}'.",
            lastException);
    }

    private static IReadOnlyList<IPAddress> OrderAddressesPreferIpv4(IReadOnlyCollection<IPAddress> addresses)
    {
        var ordered = new List<IPAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                ordered.Add(address);
            }
        }

        foreach (var address in addresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                ordered.Add(address);
            }
        }

        return ordered;
    }

    private static string GetCacheFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "cache",
            "images");

    private static string DescribeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "image source";

    private static string FormatContentLength(long? contentLength)
        => contentLength.HasValue ? $"{contentLength.Value} bytes" : "unknown length";
}
