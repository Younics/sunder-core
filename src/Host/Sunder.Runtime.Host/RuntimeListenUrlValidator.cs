using System.Net;

namespace Sunder.Runtime.Host;

internal static class RuntimeListenUrlValidator
{
    public const string DefaultListenUrl = "http://127.0.0.1:5275";

    public static IReadOnlyList<string> ParseAndValidate(string? configuredUrls, bool allowNonLoopback)
    {
        var values = string.IsNullOrWhiteSpace(configuredUrls)
            ? [DefaultListenUrl]
            : configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            throw new InvalidOperationException("Runtime listen URL is missing.");
        }

        if (values.Length != 1)
        {
            throw new InvalidOperationException("Runtime requires exactly one listen URL so authenticated connection discovery is unambiguous.");
        }

        foreach (var value in values)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException($"Runtime listen URL '{value}' must be an absolute HTTP or HTTPS origin URL.");
            }

            if (!allowNonLoopback && !IsLoopbackHost(uri.Host))
            {
                throw new InvalidOperationException(
                    $"Runtime listen URL '{value}' is not loopback. Non-loopback binding is disabled unless the explicit development-only --development-allow-non-loopback-runtime-listen override is supplied.");
            }
        }

        return values;
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}
