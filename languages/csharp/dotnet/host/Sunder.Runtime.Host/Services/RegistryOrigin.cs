namespace Sunder.Runtime.Host.Services;

internal static class RegistryOrigin
{
    public static Uri Normalize(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Registry origin must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host))
        {
            throw new ArgumentException("Non-loopback Registry origins must use HTTPS.");
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    public static Uri NormalizeAuthorizationOrigin(string? value, Uri registryOrigin)
    {
        var origin = string.IsNullOrWhiteSpace(value) ? registryOrigin : Normalize(value);
        if (!string.Equals(origin.IdnHost, registryOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(origin.Scheme, registryOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Registry authorization and API origins must use the same scheme and host.");
        }

        return origin;
    }

    public static string Key(Uri origin) => origin.AbsoluteUri;

    private static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}
