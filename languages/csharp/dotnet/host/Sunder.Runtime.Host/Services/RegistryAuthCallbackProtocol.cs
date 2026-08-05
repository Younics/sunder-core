using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host.Services;

internal static class RegistryAuthCallbackProtocol
{
    public static Uri BuildAuthorizeUri(Uri origin, Uri callback, string state, string challenge, string? displayName)
    {
        var builder = new UriBuilder(new Uri(origin, "cli/authorize"))
        {
            Query = string.Join('&',
                $"redirect_uri={Uri.EscapeDataString(callback.AbsoluteUri)}",
                $"state={Uri.EscapeDataString(state)}",
                $"code_challenge={Uri.EscapeDataString(challenge)}",
                $"display_name={Uri.EscapeDataString(string.IsNullOrWhiteSpace(displayName) ? "Sunder" : displayName.Trim())}"),
        };
        return builder.Uri;
    }

    public static async Task<(string Code, string State)> ReadAsync(
        Stream stream,
        RuntimeAuthPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var requestLine = await ReadAsciiLineAsync(stream, policy.MaxCallbackRequestLineBytes, cancellationToken)
                          ?? throw new InvalidOperationException("Registry authorization callback was empty.");
        var totalHeaderBytes = 0;
        for (var headerCount = 0; ; headerCount++)
        {
            if (headerCount >= policy.MaxCallbackHeaderCount)
            {
                throw new InvalidOperationException("Registry authorization callback included too many headers.");
            }
            var header = await ReadAsciiLineAsync(stream, policy.MaxCallbackHeaderLineBytes, cancellationToken)
                         ?? throw new InvalidOperationException("Registry authorization callback headers ended unexpectedly.");
            totalHeaderBytes = checked(totalHeaderBytes + Encoding.ASCII.GetByteCount(header) + 2);
            if (totalHeaderBytes > policy.MaxCallbackHeaderBytes)
            {
                throw new InvalidOperationException("Registry authorization callback headers were too large.");
            }
            if (header.Length == 0) return Parse(requestLine);
        }
    }

    public static async Task WriteResponseAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var body = $"<!doctype html><html><body><h1>Sunder Registry {(success ? "authorized" : "authorization failed")}</h1><p>You can close this window.</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    public static string CreateRandomToken(int byteCount)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    public static string CreateChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static (string Code, string State) Parse(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Registry authorization callback was invalid.");
        }
        var query = new Uri("http://127.0.0.1" + parts[1]).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(value => value.Length == 2)
            .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => Uri.UnescapeDataString(value[1].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);
        return query.TryGetValue("code", out var code) && query.TryGetValue("state", out var state)
            ? (code, state)
            : throw new InvalidOperationException("Registry authorization callback did not include code and state.");
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = new byte[maxBytes];
        var length = 0;
        var singleByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0) return length == 0 ? null : throw new InvalidOperationException("Registry authorization callback line ended unexpectedly.");
            if (singleByte[0] == (byte)'\n')
            {
                if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                return Encoding.ASCII.GetString(bytes, 0, length);
            }
            if (length >= maxBytes) throw new InvalidOperationException("Registry authorization callback line was too long.");
            bytes[length++] = singleByte[0];
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
