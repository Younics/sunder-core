using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Format;

namespace Sunder.App.Services;

internal sealed class AppWebContentServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxConcurrentRequests = 32;
    private const int CopyBufferSize = 81920;
    private const long MaxHtmlBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(1);
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self'; font-src 'self'; connect-src 'self'; media-src 'self'; " +
        "worker-src 'self'; manifest-src 'self'; frame-src 'none'; child-src 'none'; " +
        "object-src 'none'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requests = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly IReadOnlyDictionary<string, ContentFile> _files;
    private readonly IReadOnlySet<string> _routes;
    private readonly string _entryPoint;
    private readonly Task _acceptLoop;
    private int _disposed;

    private AppWebContentServer(
        TcpListener listener,
        string pathToken,
        IReadOnlyDictionary<string, ContentFile> files,
        IReadOnlySet<string> routes,
        string entryPoint)
    {
        _listener = listener;
        _files = files;
        _routes = routes;
        _entryPoint = entryPoint;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Origin = new Uri($"http://127.0.0.1:{endpoint.Port}/", UriKind.Absolute);
        BasePath = $"/{pathToken}/";
        BaseUri = new Uri(Origin, BasePath);
        _acceptLoop = AcceptAsync(_lifetime.Token);
    }

    public Uri Origin { get; }

    public string BasePath { get; }

    public Uri BaseUri { get; }

    public static async Task<AppWebContentServer> StartAsync(
        string contentRoot,
        SunderPackageTargetManifest target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        var files = await IndexFilesAsync(root, cancellationToken).ConfigureAwait(false);
        var entryPoint = ParseLogicalPath(target.EntryPoint, "web target entry point");
        if (!files.ContainsKey(entryPoint))
        {
            throw new InvalidDataException($"Web target entry point '{entryPoint}' is missing from its selected projection.");
        }

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in target.Views ?? [])
        {
            var routeValue = view?.Route;
            if (!SunderPackageFormat.IsWebViewRoute(routeValue))
            {
                throw new InvalidDataException("Web target contains invalid view route metadata.");
            }
            var route = routeValue == "/" ? string.Empty : routeValue![1..];
            if (!routes.Add(route))
            {
                throw new InvalidDataException($"Web target route '{routeValue}' is declared more than once.");
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(128);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new AppWebContentServer(listener, token, files, routes, entryPoint);
    }

    public Uri GetRouteUri(string route)
    {
        if (!SunderPackageFormat.IsWebViewRoute(route))
        {
            throw new ArgumentException("The web view route is invalid.", nameof(route));
        }
        return route == "/" ? BaseUri : new Uri(BaseUri, route[1..]);
    }

    public bool Owns(Uri? uri)
        => uri is not null
           && uri.IsAbsoluteUri
           && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
           && string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
           && uri.Port == Origin.Port
           && uri.AbsolutePath.StartsWith(BasePath, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        for (var index = 0; index < MaxConcurrentRequests; index++)
        {
            await _requests.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _requests.Dispose();
        _lifetime.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            _ = HandleOwnedAsync(client, cancellationToken);
        }
    }

    private async Task HandleOwnedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(RequestTimeout);
                await HandleAsync(client.GetStream(), requestTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            // A slow or abandoned loopback client must not retain a request slot indefinitely.
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "A package web content request failed.",
                exception,
                visibleInDeveloperLog: false);
        }
        finally
        {
            _requests.Release();
        }
    }

    private async Task HandleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            await WriteStatusAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || request[2] is not "HTTP/1.0" and not "HTTP/1.1")
        {
            await WriteStatusAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }
        var method = request[0];
        if (method is not "GET" and not "HEAD")
        {
            await WriteStatusAsync(
                stream,
                405,
                "Method Not Allowed",
                cancellationToken,
                "Allow: GET, HEAD\r\n").ConfigureAwait(false);
            return;
        }
        if (!HasExpectedHost(lines.Skip(1)))
        {
            await WriteStatusAsync(stream, 421, "Misdirected Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var rawTarget = request[1];
        if (rawTarget.Contains('?', StringComparison.Ordinal)
            || rawTarget.Contains('#', StringComparison.Ordinal)
            || rawTarget.Contains('%', StringComparison.Ordinal)
            || rawTarget.Contains('\\', StringComparison.Ordinal)
            || !rawTarget.StartsWith(BasePath, StringComparison.Ordinal))
        {
            await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }
        var logicalPath = rawTarget[BasePath.Length..];
        if (_routes.Contains(logicalPath))
        {
            logicalPath = _entryPoint;
        }
        else if (!TryParseRequestedPath(logicalPath, out logicalPath))
        {
            await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_files.TryGetValue(logicalPath, out var file))
        {
            await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        var isHtml = string.Equals(Path.GetExtension(file.LogicalPath), ".html", StringComparison.OrdinalIgnoreCase);
        FileStream input;
        try
        {
            input = await OpenValidatedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteStatusAsync(stream, 409, "Conflict", cancellationToken).ConfigureAwait(false);
            return;
        }
        await using var validatedInput = input;
        byte[]? content = null;
        var contentLength = file.Length;
        if (isHtml)
        {
            try
            {
                content = InjectBase(await ReadHtmlAsync(validatedInput, file.Length, cancellationToken).ConfigureAwait(false));
                contentLength = content.LongLength;
            }
            catch (InvalidDataException)
            {
                await WriteStatusAsync(stream, 409, "Conflict", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var headers = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Connection: close\r\n")
            .Append("Content-Type: ").Append(ContentType(file.LogicalPath)).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Cross-Origin-Opener-Policy: same-origin\r\n")
            .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
            .Append("Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()\r\n")
            .Append("Content-Security-Policy: ").Append(ContentSecurityPolicy).Append("\r\n")
            .Append(isHtml
                ? "Cache-Control: no-store\r\nPragma: no-cache\r\n"
                : "Cache-Control: public, max-age=31536000, immutable\r\n")
            .Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken).ConfigureAwait(false);
        if (method == "GET")
        {
            if (content is not null)
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await validatedInput.CopyToAsync(stream, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool HasExpectedHost(IEnumerable<string> lines)
    {
        string? host = null;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }
            if (!line[..separator].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (host is not null)
            {
                return false;
            }
            host = line[(separator + 1)..].Trim();
        }
        return string.Equals(host, $"127.0.0.1:{Origin.Port}", StringComparison.Ordinal);
    }

    private byte[] InjectBase(byte[] content)
    {
        string html;
        try
        {
            html = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Package HTML is not valid UTF-8.", exception);
        }
        var baseElement = $"<base href=\"{BasePath}\">";
        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            var close = html.IndexOf('>', head);
            if (close >= 0)
            {
                html = html.Insert(close + 1, baseElement);
                return Encoding.UTF8.GetBytes(html);
            }
        }
        return Encoding.UTF8.GetBytes(baseElement + html);
    }

    private static async Task<IReadOnlyDictionary<string, ContentFile>> IndexFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Package web content root is missing or linked.");
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package web content contains a linked directory.");
            }
        }

        var files = new Dictionary<string, ContentFile>(StringComparer.Ordinal);
        var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package web content contains a linked file.");
            }
            var logicalPath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (logicalPath == SunderPackageFormat.ManifestPath)
            {
                continue;
            }
            logicalPath = ParseLogicalPath(logicalPath, "web content path");
            if (!casePaths.Add(logicalPath))
            {
                throw new InvalidDataException($"Package web content has a case-colliding path '{logicalPath}'.");
            }
            var info = new FileInfo(fullPath);
            await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var observedLength = input.Length;
            if (string.Equals(info.Extension, ".html", StringComparison.OrdinalIgnoreCase)
                && observedLength > MaxHtmlBytes)
            {
                throw new InvalidDataException($"Package HTML content exceeds the {MaxHtmlBytes}-byte limit.");
            }
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            files.Add(logicalPath, new ContentFile(logicalPath, Path.GetFullPath(fullPath), observedLength, hash));
        }
        return files;
    }

    private static async Task<FileStream> OpenValidatedAsync(
        ContentFile file,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(file.FullPath)
            || (File.GetAttributes(file.FullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Package web content changed after activation.");
        }
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Length)
            {
                throw new InvalidDataException("Package web content changed after activation.");
            }
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Package web content failed its activation hash fence.");
            }
            stream.Position = 0;
            return stream;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                              or DirectoryNotFoundException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            stream?.Dispose();
            throw new InvalidDataException("Package web content changed after activation.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadHtmlAsync(
        Stream stream,
        long length,
        CancellationToken cancellationToken)
    {
        if (length > MaxHtmlBytes)
        {
            throw new InvalidDataException($"Package HTML content exceeds the {MaxHtmlBytes}-byte limit.");
        }
        var content = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            length += read;
            if (length >= 4
                && bytes[length - 4] == '\r'
                && bytes[length - 3] == '\n'
                && bytes[length - 2] == '\r'
                && bytes[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes, 0, length - 2);
            }
        }
        return null;
    }

    private static bool TryParseRequestedPath(string value, out string logicalPath)
    {
        logicalPath = string.Empty;
        if (value.Length == 0
            || !ArchiveRelativePath.TryParse(
                value,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth,
                out var parsed,
                out _))
        {
            return false;
        }
        logicalPath = parsed.ToString();
        return string.Equals(logicalPath, value, StringComparison.Ordinal);
    }

    private static string ParseLogicalPath(string? value, string label)
    {
        if (!ArchiveRelativePath.TryParse(
                value,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth,
                out var parsed,
                out var error))
        {
            throw new InvalidDataException($"Package {label} is invalid: {error}.");
        }
        return parsed.ToString();
    }

    private static string ContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".wasm" => "application/wasm",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };

    private static async Task WriteStatusAsync(
        Stream stream,
        int status,
        string reason,
        CancellationToken cancellationToken,
        string additionalHeaders = "")
    {
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n{additionalHeaders}\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ContentFile(string LogicalPath, string FullPath, long Length, string Sha256);
}
