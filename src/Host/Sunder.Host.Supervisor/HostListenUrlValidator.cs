using System.Net;

namespace Sunder.Host.Supervisor;

internal static class HostListenUrlValidator
{
    public const string DefaultListenUrl = "http://127.0.0.1:5275";

    public static string ParseAndValidateLocal(string? configuredUrls)
    {
        var values = string.IsNullOrWhiteSpace(configuredUrls)
            ? [DefaultListenUrl]
            : configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 1
            || !Uri.TryCreate(values[0], UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("The local Sunder Host requires exactly one HTTP origin URL.");
        }

        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)))
        {
            throw new InvalidOperationException(
                "Non-loopback Sunder Host binding is not available until production TLS and pairing are enabled.");
        }

        return values[0];
    }
}
