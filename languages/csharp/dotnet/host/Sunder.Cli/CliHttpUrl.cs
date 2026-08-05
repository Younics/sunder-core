namespace Sunder.Cli;

internal static class CliHttpUrl
{
    public static Uri RequireBase(Uri uri, string label)
    {
        Require(uri, label);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{label} URL '{uri}' cannot contain a query string or fragment.");
        }
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)) return uri;
        return new UriBuilder(uri) { Path = $"{uri.AbsolutePath}/" }.Uri;
    }

    public static Uri Require(Uri uri, string label)
    {
        if (!uri.IsAbsoluteUri
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{label} URL '{uri}' must use HTTP or HTTPS.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"{label} URL '{uri}' cannot contain user information.");
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.IsLoopback)
        {
            throw new InvalidOperationException($"{label} URL '{uri}' must use HTTPS unless the host is loopback.");
        }

        return uri;
    }
}
